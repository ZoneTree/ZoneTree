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
      while (true)
      {
        var root = Root;
        root.ReadLock();
        if (root != Root)
        {
          root.ReadUnlock();
          continue;
        }
        return ContainsKey(root, in key);
      }
    }
    finally
    {
      topLevelLocker.ReadUnlock();
    }
  }

  bool ContainsKey(Node node, in TKey key)
  {
    while (node != null)
    {
      var found = node.TryGetPosition(Comparer, in key, out var position);
      if (node is LeafNode)
      {
        node.ReadUnlock();
        return found;
      }
      if (found)
      {
        // if key position is found with exact match
        // continue with right child.
        ++position;
      }
      var previousNode = node;
      node = node.Children[position];
      node.ReadLock();
      previousNode.ReadUnlock();
    }
    return false;
  }

  public bool TryGetValue(in TKey key, out TValue value)
  {
    var topLevelLocker = TopLevelLocker;
    try
    {
      topLevelLocker.ReadLock();
      while (true)
      {
        var root = Root;
        root.ReadLock();
        if (root != Root)
        {
          root.ReadUnlock();
          continue;
        }
        return TryGetValue(root, in key, out value);
      }
    }
    finally
    {
      topLevelLocker.ReadUnlock();
    }
  }

  bool TryGetValue(Node node, in TKey key, out TValue value)
  {
    while (node != null)
    {
      var found = node.TryGetPosition(Comparer, in key, out var position);
      if (node is LeafNode leaf)
      {
        if (!found)
        {
          node.ReadUnlock();
          value = default;
          return false;
        }
        value = leaf.Values[position];
        node.ReadUnlock();
        return true;
      }
      if (found)
      {
        // if key position is found with exact match
        // continue with right child.
        ++position;
      }
      var child = node.Children[position];
      child.ReadLock();
      node.ReadUnlock();
      node = child;
    }
    value = default;
    return false;
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
      while (true)
      {
        var root = Root;
        root.ReadLock();
        if (root != Root)
        {
          root.ReadUnlock();
          continue;
        }
        return GetLeafNode(root, in key);
      }
    }
    finally
    {
      topLevelLocker.ReadUnlock();
    }
  }

  LeafNode GetLeafNode(Node node, in TKey key)
  {
    while (true)
    {
      var found = node.TryGetPosition(Comparer, in key, out var position);
      if (node is LeafNode leaf)
      {
        node.ReadUnlock();
        return leaf;
      }
      if (found)
      {
        // if key position is found with exact match
        // continue with right child.
        ++position;
      }
      var previousNode = node;
      node = node.Children[position];
      node.ReadLock();
      previousNode.ReadUnlock();
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
