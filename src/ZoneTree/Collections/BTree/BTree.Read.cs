using ZoneTree.Collections.BTree.Lock;

namespace ZoneTree.Collections.BTree;

/// <summary>
/// In memory B+Tree.
/// This class is thread-safe.
/// </summary>
/// <typeparam name="TKey">Key Type</typeparam>
/// <typeparam name="TValue">Value Type</typeparam>
public sealed partial class BTree<TKey, TValue>
{
  // Writes are short and a leaf rarely changes twice during a read, so a read
  // that fails this many times is competing with a stalled or very busy writer.
  const int MaximumOptimisticReadCount = 8;

  public bool ContainsKey(in TKey key)
  {
    ReadRecord(FindLeaf(in key), in key, false, out var isExactMatch, out _, out _);
    return isExactMatch;
  }

  public bool TryGetValue(in TKey key, out TValue value)
  {
    ReadRecord(FindLeaf(in key), in key, false, out var isExactMatch, out _, out value);
    if (isExactMatch)
      return true;
    value = default;
    return false;
  }

  /// <summary>
  /// Copies the first record of a leaf with a key greater than or equal to the
  /// given key, or greater than it. Readers do not lock the leaf unless writers
  /// keep changing it, so they do not contend with writers on the same leaf.
  /// </summary>
  /// <param name="leaf">The leaf.</param>
  /// <param name="key">The key.</param>
  /// <param name="skipKey">Skips the record with the given key.</param>
  /// <param name="isExactMatch">True if the leaf has the given key.</param>
  /// <param name="recordKey">The key of the record.</param>
  /// <param name="recordValue">The value of the record.</param>
  /// <returns>True if the leaf has such a record.</returns>
  bool ReadRecord(
      LeafNode leaf,
      in TKey key,
      bool skipKey,
      out bool isExactMatch,
      out TKey recordKey,
      out TValue recordValue)
  {
    var spinWait = new SpinWait();
    for (var i = 0; i < MaximumOptimisticReadCount; ++i)
    {
      var version = leaf.Version;
      if ((version & 1) != 0)
      {
        spinWait.SpinOnce();
        continue;
      }
      bool hasRecord;
      try
      {
        hasRecord = CopyRecord(leaf, in key, skipKey, out isExactMatch, out recordKey, out recordValue);
      }
      catch (Exception) when (!leaf.IsUnchangedSince(version))
      {
        // The comparer can fail on a key that the concurrent change tore.
        continue;
      }
      if (leaf.IsUnchangedSince(version))
        return hasRecord;
    }

    var topLevelLocker = TopLevelLocker;
    try
    {
      topLevelLocker.ReadLock();
      leaf.ReadLock();
      try
      {
        return CopyRecord(leaf, in key, skipKey, out isExactMatch, out recordKey, out recordValue);
      }
      finally
      {
        leaf.ReadUnlock();
      }
    }
    finally
    {
      topLevelLocker.ReadUnlock();
    }
  }

  bool CopyRecord(
      LeafNode leaf,
      in TKey key,
      bool skipKey,
      out bool isExactMatch,
      out TKey recordKey,
      out TValue recordValue)
  {
    isExactMatch = leaf.TryGetPosition(Comparer, in key, out var position);
    if (isExactMatch && skipKey)
      ++position;
    if (position >= leaf.Length)
    {
      recordKey = default;
      recordValue = default;
      return false;
    }
    recordKey = leaf.Keys[position];
    recordValue = leaf.Values[position];
    return true;
  }

  /// <summary>
  /// Finds the leaf of a key without locking the inner nodes, so
  /// reads and writes do not contend with each other at the root.
  /// </summary>
  /// <remarks>
  /// A node reached this way was current at some moment after the search began,
  /// and a split leaves the replaced node as it was. So the leaf holds every
  /// write that completed before the search began, even if a split replaces it.
  /// </remarks>
  LeafNode FindLeaf(in TKey key)
  {
    var node = Root;
    var spinWait = new SpinWait();
    while (node is not LeafNode)
    {
      var version = node.Version;
      if ((version & 1) != 0)
      {
        spinWait.SpinOnce();
        continue;
      }
      Node child;
      try
      {
        var found = node.TryGetPosition(Comparer, in key, out var position);
        // With an exact match, continue with the right child.
        child = node.Children[found ? position + 1 : position];
      }
      catch (Exception) when (!node.IsUnchangedSince(version))
      {
        // The comparer can fail on a key that the concurrent change tore.
        continue;
      }
      if (node.IsUnchangedSince(version))
        node = child;
    }
    return (LeafNode)node;
  }

  /// <summary>
  /// Visits the records with keys greater than or equal to the given key in
  /// ascending order. Unlike an iterator, it does not copy the leaves: it copies
  /// one record at a time, and the visitor runs on the copy without any lock.
  /// </summary>
  /// <remarks>
  /// Each record is found by its key after the previous one, so writes
  /// during the visit cannot make it miss a record or visit one twice.
  /// </remarks>
  /// <returns>
  /// The action that ended the visit, or
  /// <see cref="SegmentVisitAction.NextSegment"/> after the last record.
  /// </returns>
  public SegmentVisitAction Visit<TVisitor>(in TKey key, ref TVisitor visitor)
      where TVisitor : struct, ISegmentRecordVisitor<TKey, TValue>
  {
    var leaf = FindLeaf(in key);
    var lastKey = key;
    var skipKey = false;
    while (leaf != null)
    {
      if (!ReadRecord(leaf, in lastKey, skipKey, out _, out var recordKey, out var recordValue))
      {
        leaf = leaf.Next;
        continue;
      }
      var action = visitor.Visit(in recordKey, in recordValue);
      if (action != SegmentVisitAction.NextRecord)
        return action;
      lastKey = recordKey;
      skipKey = true;
    }
    return SegmentVisitAction.NextSegment;
  }

  LeafNode GetLeafNode(in TKey key)
  {
    var topLevelLocker = TopLevelLocker;
    try
    {
      topLevelLocker.ReadLock();
      return FindLeaf(in key);
    }
    finally
    {
      topLevelLocker.ReadUnlock();
    }
  }

  LeafNode GetFrozenLeafNode(in TKey key)
  {
    var root = Root;
    return GetFrozenLeafNode(root, in key);
  }

  LeafNode GetFrozenLeafNode(Node node, in TKey key)
  {
    while (true)
    {
      var found = node.TryGetPosition(Comparer, in key, out var position);
      if (node is LeafNode leaf)
      {
        return leaf;
      }
      if (found)
      {
        // if key position is found with exact match
        // continue with right child.
        ++position;
      }
      node = node.Children[position];
    }
  }

  /// <summary>
  /// Converts BTree to lock-free BTree by removing top level locks and
  /// all locks from nodes. It is caller's responsibility
  /// to not to modify the tree after this method is called.
  /// It is also caller's responsibility to ensure all ongoing writes
  /// are already finished.
  /// </summary>
  public void SetTreeReadOnlyAndLockFree()
  {
    TopLevelLocker = NoLock.Instance;
    var newRoot = Root.CloneWithNoLock();

    // The cloned leaves are not linked yet.
    var leaves = CollectLeaves(newRoot);
    for (var i = 1; i < leaves.Count; ++i)
    {
      leaves[i - 1].Next = leaves[i];
      leaves[i].Previous = leaves[i - 1];
    }
    FirstLeafNode = leaves[0];
    LastLeafNode = leaves[^1];
    Root = newRoot;
    IsReadOnly = true;
  }

  public void Validate()
  {
    Root.Validate(Comparer);
  }

  /// <summary>
  /// Checks that the leaf list links the leaves of the tree in order in both
  /// directions, from the first leaf to the last. The caller ensures that
  /// no write runs during the check.
  /// </summary>
  public void ValidateLeafs()
  {
    var leaves = CollectLeaves(Root);
    if (FirstLeafNode != leaves[0])
      throw new Exception("The first leaf is not the leftmost leaf of the tree.");
    if (LastLeafNode != leaves[^1])
      throw new Exception("The last leaf is not the rightmost leaf of the tree.");
    if (leaves[0].Previous != null)
      throw new Exception("The first leaf links to a previous leaf.");
    if (leaves[^1].Next != null)
      throw new Exception("The last leaf links to a next leaf.");
    for (var i = 1; i < leaves.Count; ++i)
    {
      var left = leaves[i - 1];
      var right = leaves[i];
      if (left == right)
        throw new Exception($"Leaf {i} appears twice in the tree.");
      if (left.Next != right)
        throw new Exception($"Leaf {i - 1} does not link to leaf {i} as its next leaf.");
      if (right.Previous != left)
        throw new Exception($"Leaf {i} does not link to leaf {i - 1} as its previous leaf.");
    }
  }

  /// <summary>
  /// Returns the leaves under a node from left to right.
  /// All leaves are at the same depth, so a breadth-first walk meets them in order.
  /// </summary>
  static List<LeafNode> CollectLeaves(Node root)
  {
    var leaves = new List<LeafNode>();
    var queue = new Queue<Node>();
    queue.Enqueue(root);
    while (queue.Count > 0)
    {
      var node = queue.Dequeue();
      if (node is LeafNode leaf)
      {
        leaves.Add(leaf);
        continue;
      }
      for (var i = 0; i <= node.Length; ++i)
        queue.Enqueue(node.Children[i]);
    }
    return leaves;
  }
}
