using ZoneTree.Exceptions;

namespace ZoneTree.Collections.BTree;

/// <summary>
/// In memory B+Tree.
/// This class is thread-safe.
/// </summary>
/// <typeparam name="TKey">Key Type</typeparam>
/// <typeparam name="TValue">Value Type</typeparam>
public sealed partial class BTree<TKey, TValue>
{
  public bool Upsert(in TKey key, in TValue value, out long opIndex)
  {
    if (IsReadOnly)
      throw new BTreeIsReadOnlyException();
    try
    {
      WriteLock();
      while (true)
      {
        var root = Root;
        root.WriteLock();
        if (root != Root)
        {
          root.WriteUnlock();
          continue;
        }

        if (root.IsFull)
          root = SplitRoot(root);
        return UpsertNonFull(root, in key, in value, out opIndex);
      }
    }
    finally
    {
      WriteUnlock();
    }
  }

  public bool TryInsert(in TKey key, in TValue value, out long opIndex)
  {
    if (IsReadOnly)
      throw new BTreeIsReadOnlyException();
    try
    {
      WriteLock();
      while (true)
      {
        var root = Root;
        root.WriteLock();
        if (root != Root)
        {
          root.WriteUnlock();
          continue;
        }
        if (root.IsFull)
          root = SplitRoot(root);
        return TryInsertNonFull(root, in key, in value, out opIndex);
      }
    }
    finally
    {
      WriteUnlock();
    }
  }

  public delegate void AddDelegate(ref TValue value);

  public delegate void UpdateDelegate(ref TValue value);

  public AddOrUpdateResult AddOrUpdate(
      in TKey key,
      AddDelegate adder,
      UpdateDelegate updater,
      out long opIndex)
  {
    if (IsReadOnly)
      throw new BTreeIsReadOnlyException();
    try
    {
      WriteLock();
      while (true)
      {
        var root = Root;
        root.WriteLock();
        if (root != Root)
        {
          root.WriteUnlock();
          continue;
        }
        if (root.IsFull)
          root = SplitRoot(root);
        return TryAddOrUpdateNonFull(root, in key, adder, updater, out opIndex);
      }
    }
    finally
    {
      WriteUnlock();
    }
  }

  /// <summary>
  /// Replaces the full root with a new root over its halves. The caller holds
  /// the write lock of the full root, and gets the write lock of the new root.
  /// </summary>
  /// <remarks>
  /// The new root is published before the caller descends, so a write that
  /// fails below it leaves a valid tree, and writers waiting for the old root
  /// start over from the new one.
  /// </remarks>
  Node SplitRoot(Node root)
  {
    var newRoot = new Node(GetNodeLocker(), NodeSize);
    newRoot.Children[0] = root;
    newRoot.WriteLock();
    // The root has no neighbours, so its split succeeds.
    TrySplitChild(newRoot, 0, root, out _);
    Root = newRoot;
    root.WriteUnlock();
    return newRoot;
  }

  /// <summary>
  /// Splits the full child of a parent. The caller holds the write locks
  /// of both, and keeps them when the split fails.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A leaf split also relinks the neighbour leaves. The links of a leaf
  /// change only while it is locked, so the neighbours of the locked child
  /// stay its neighbours until the split ends. Iterators follow the links
  /// without locks, so the new leaves are filled and linked to each other
  /// and to the neighbours before the neighbours link to them.
  /// </para>
  /// <para>
  /// While holding a leaf, a writer waits only for the leaf on its left,
  /// and an iterator copying a leaf waits for nothing, so the left neighbour
  /// is waited for. The right neighbour is taken only when it is free,
  /// because its holder may be splitting it and waiting for the child as its
  /// left neighbour. Then the split fails with
  /// <paramref name="busyNeighbour"/>, and the caller has to release the
  /// child and wait for the neighbour with <see cref="WaitForRelease"/>
  /// before trying again. Otherwise the caller could lock the child again
  /// before the waiting holder gets it, and the two splits would retry forever.
  /// </para>
  /// </remarks>
  bool TrySplitChild(
      Node parent, int parentInsertPosition, Node child, out LeafNode busyNeighbour)
  {
    busyNeighbour = null;
    var pivotPosition = (child.Length + 1) / 2;
    ref var pivotKey = ref child.Keys[pivotPosition];
    if (child is LeafNode childLeaf)
    {
      var pre = childLeaf.Previous;
      pre?.WriteLock();
      var next = childLeaf.Next;
      if (next != null && !next.TryEnterWriteLock(0))
      {
        pre?.WriteUnlock();
        busyNeighbour = next;
        return false;
      }

      (var left, var right) = childLeaf
          .SplitLeaf(pivotPosition, LeafSize, GetNodeLocker(), GetNodeLocker());

      left.Previous = pre;
      right.Next = next;

      if (pre == null)
        FirstLeafNode = left;
      else
        pre.Next = left;

      if (next == null)
        LastLeafNode = right;
      else
        next.Previous = right;

      next?.WriteUnlock();
      pre?.WriteUnlock();

      parent.InsertKeyAndChild(parentInsertPosition, in pivotKey, left, right);
    }
    else
    {
      (var left, var right) = child
          .Split(pivotPosition, NodeSize, GetNodeLocker(), GetNodeLocker());
      parent.InsertKeyAndChild(parentInsertPosition, in pivotKey, left, right);
    }
    return true;
  }

  /// <summary>
  /// Waits until the holder of a leaf releases it. The caller holds an inner
  /// node only. A writer holding a leaf waits only for other leaves, and an
  /// iterator copying a leaf waits for nothing, so the holder finishes
  /// without waiting for the caller.
  /// </summary>
  static void WaitForRelease(LeafNode leaf)
  {
    leaf.WriteLock();
    leaf.WriteUnlock();
  }

  bool UpsertNonFull(Node node, in TKey key, in TValue value, out long opIndex)
  {
    try
    {
      while (true)
      {
        var found = node.TryGetPosition(Comparer, in key, out var position);
        if (node is LeafNode leaf)
        {
          opIndex = OpIndexProvider.NextId();
          if (found)
          {
            leaf.Update(position, in key, in value);
            node.WriteUnlock();
            return false;
          }
          leaf.Insert(position, in key, in value);
          Interlocked.Increment(ref _length);
          node.WriteUnlock();
          return true;
        }
        if (found)
          ++position;
        var child = node.Children[position];
        child.WriteLock();
        if (child.IsFull)
        {
          var splitted = TrySplitChild(node, position, child, out var busyNeighbour);
          child.WriteUnlock();
          if (!splitted)
          {
            WaitForRelease(busyNeighbour);
            continue;
          }

          if (Comparer.Compare(in key, in node.Keys[position]) >= 0)
            ++position;

          child = node.Children[position];
          child.WriteLock();
        }
        node.WriteUnlock();
        node = child;
      }
    }
    catch
    {
      // The comparer runs while only the current node is locked.
      node.WriteUnlock();
      throw;
    }
  }

  bool TryInsertNonFull(Node node, in TKey key, in TValue value, out long opIndex)
  {
    try
    {
      while (true)
      {
        var found = node.TryGetPosition(Comparer, in key, out var position);
        if (node is LeafNode leaf)
        {
          if (found)
          {
            node.WriteUnlock();
            opIndex = 0;
            return false;
          }

          opIndex = OpIndexProvider.NextId();
          leaf.Insert(position, in key, in value);
          Interlocked.Increment(ref _length);
          node.WriteUnlock();
          return true;
        }

        if (found)
          ++position;

        var child = node.Children[position];
        child.WriteLock();
        if (child.IsFull)
        {
          var splitted = TrySplitChild(node, position, child, out var busyNeighbour);
          child.WriteUnlock();
          if (!splitted)
          {
            WaitForRelease(busyNeighbour);
            continue;
          }

          if (Comparer.Compare(in key, in node.Keys[position]) >= 0)
            ++position;

          child = node.Children[position];
          child.WriteLock();
        }
        node.WriteUnlock();
        node = child;
      }
    }
    catch
    {
      // The comparer runs while only the current node is locked.
      node.WriteUnlock();
      throw;
    }
  }

  AddOrUpdateResult TryAddOrUpdateNonFull(
      Node node, in TKey key, AddDelegate adder, UpdateDelegate updater,
      out long opIndex)
  {
    try
    {
      while (true)
      {
        var found = node.TryGetPosition(Comparer, in key, out var position);
        if (node is LeafNode leaf)
        {
          opIndex = OpIndexProvider.NextId();
          if (found)
          {
            // The updater runs on a copy: readers do not lock the leaf,
            // so they must not see the value while the updater changes it.
            var updatedValue = leaf.Values[position];
            updater(ref updatedValue);
            leaf.UpdateValue(position, in updatedValue);
            node.WriteUnlock();
            return AddOrUpdateResult.UPDATED;
          }
          TValue value = default;
          adder(ref value);
          leaf.Insert(position, in key, in value);
          Interlocked.Increment(ref _length);
          node.WriteUnlock();
          return AddOrUpdateResult.ADDED;
        }

        if (found)
          ++position;

        var child = node.Children[position];
        child.WriteLock();
        if (child.IsFull)
        {
          var splitted = TrySplitChild(node, position, child, out var busyNeighbour);
          child.WriteUnlock();
          if (!splitted)
          {
            WaitForRelease(busyNeighbour);
            continue;
          }

          if (Comparer.Compare(in key, in node.Keys[position]) >= 0)
            ++position;

          child = node.Children[position];
          child.WriteLock();
        }
        node.WriteUnlock();
        node = child;
      }
    }
    catch
    {
      // The comparer and the callbacks run while only the current node is locked.
      node.WriteUnlock();
      throw;
    }
  }
}
