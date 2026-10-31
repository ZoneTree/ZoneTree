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
  public bool ContainsKey(in TKey key)
  {
    var topLevelLocker = TopLevelLocker;
    try
    {
      topLevelLocker.ReadLock();
      var leaf = FindLeaf(in key);
      leaf.ReadLock();
      try
      {
        return leaf.TryGetPosition(Comparer, in key, out _);
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

  public bool TryGetValue(in TKey key, out TValue value)
  {
    var topLevelLocker = TopLevelLocker;
    try
    {
      topLevelLocker.ReadLock();
      var leaf = FindLeaf(in key);
      leaf.ReadLock();
      try
      {
        if (leaf.TryGetPosition(Comparer, in key, out var position))
        {
          value = leaf.Values[position];
          return true;
        }
        value = default;
        return false;
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

  /// <summary>
  /// Finds the leaf of a key without locking the inner nodes, so
  /// readers do not contend with each other and with writers at the root.
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
  /// ascending order. Unlike an iterator, it does not copy the leaves: the
  /// visitor runs under the read lock of the leaf, and no lock is held across leaves.
  /// </summary>
  /// <returns>
  /// The action that ended the visit, or
  /// <see cref="SegmentVisitAction.NextSegment"/> after the last record.
  /// </returns>
  public SegmentVisitAction Visit<TVisitor>(in TKey key, ref TVisitor visitor)
      where TVisitor : struct, ISegmentRecordVisitor<TKey, TValue>
  {
    var leaf = GetLeafNode(in key);
    var isFirstLeaf = true;
    while (leaf != null)
    {
      var topLevelLocker = TopLevelLocker;
      try
      {
        topLevelLocker.ReadLock();
        leaf.ReadLock();
        try
        {
          var position = 0;
          if (isFirstLeaf)
            leaf.TryGetPosition(Comparer, in key, out position);
          var length = leaf.Length;
          for (; position < length; ++position)
          {
            var action = visitor.Visit(in leaf.Keys[position], in leaf.Values[position]);
            if (action != SegmentVisitAction.NextRecord)
              return action;
          }
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
      isFirstLeaf = false;
      leaf = leaf.Next;
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
