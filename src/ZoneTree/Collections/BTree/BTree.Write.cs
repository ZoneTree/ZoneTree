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
    var leaf = TryLockLeafWithRoom(in key);
    if (leaf != null)
      return UpsertNonFull(leaf, in key, in value, out opIndex);
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
    var leaf = TryLockLeafWithRoom(in key);
    if (leaf != null)
      return TryInsertNonFull(leaf, in key, in value, out opIndex);
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
    var leaf = TryLockLeafWithRoom(in key);
    if (leaf != null)
      return TryAddOrUpdateNonFull(leaf, in key, adder, updater, out opIndex);
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
  /// Finds the leaf of a key without locking the inner nodes and locks it, so
  /// writes to different leaves do not contend at the root. The caller holds nothing
  /// else and waits for nothing else while it holds the leaf.
  /// </summary>
  /// <remarks>
  /// A leaf with room is the right leaf for the key. The keys of a leaf change only
  /// by a split, and leaves never merge. A split replaces a full leaf and leaves it
  /// as it was, so a leaf that a concurrent split replaced after the search is full.
  /// </remarks>
  /// <returns>
  /// The locked leaf, or null if it is full or the tree has no node locks.
  /// Then the caller goes through the root, because a split needs the parent.
  /// </returns>
  LeafNode TryLockLeafWithRoom(in TKey key)
  {
    if (!HasNodeLocks)
      return null;
    var lastLeaf = TryLockLastLeaf(in key);
    if (lastLeaf != null)
      return lastLeaf;
    var leaf = FindLeaf(in key);
    leaf.WriteLock();
    if (!leaf.IsFull)
      return leaf;
    leaf.WriteUnlock();
    return null;
  }

  /// <summary>
  /// Locks the last leaf for a key at or after its last key, without
  /// searching the tree, so ascending inserts skip the search of the inner nodes.
  /// </summary>
  /// <remarks>
  /// A last leaf with room is still the last leaf, because only its split could
  /// change that and a split replaces a full leaf. It covers every key from its
  /// first key upward, so a key at or after its last key belongs to it. The first
  /// comparison reads a key without the lock, which only decides whether to try.
  /// While the root is a leaf, its first split can link the new leaves to a new
  /// root that is not published yet, so a search could not reach them.
  /// </remarks>
  /// <returns>The locked leaf with room, or null if the key needs a search.</returns>
  LeafNode TryLockLastLeaf(in TKey key)
  {
    var leaf = LastLinkedLeafNode;
    if (Root is LeafNode)
      return null;
    var length = leaf.Length;
    if (length == 0 || length == leaf.Keys.Length)
      return null;
    try
    {
      if (Comparer.Compare(in key, in leaf.Keys[length - 1]) < 0)
        return null;
    }
    catch (Exception)
    {
      // The key may be torn. The search reports a failure of the comparer.
      return null;
    }
    leaf.WriteLock();
    try
    {
      if (!leaf.IsFull && Comparer.Compare(in key, in leaf.Keys[leaf.Length - 1]) >= 0)
        return leaf;
    }
    catch
    {
      leaf.WriteUnlock();
      throw;
    }
    leaf.WriteUnlock();
    return null;
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
      if (next == null)
        LastLinkedLeafNode = right;
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
          if (child.IsFull)
          {
            UnlockFilledSplitChild(node, child);
            node = null;
            return Upsert(in key, in value, out opIndex);
          }
        }
        node.WriteUnlock();
        node = child;
      }
    }
    catch
    {
      // The comparer runs while only the current node is locked.
      node?.WriteUnlock();
      throw;
    }
  }

  /// <summary>
  /// Unlocks a new leaf and its parent when the leaf is full again. Writers reach
  /// a new leaf without its parent, so they can fill it before the writer that split
  /// its old leaf locks it. That writer starts over from the root, because the
  /// parent may have no room for another split.
  /// </summary>
  static void UnlockFilledSplitChild(Node parent, Node child)
  {
    child.WriteUnlock();
    parent.WriteUnlock();
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
          if (child.IsFull)
          {
            UnlockFilledSplitChild(node, child);
            node = null;
            return TryInsert(in key, in value, out opIndex);
          }
        }
        node.WriteUnlock();
        node = child;
      }
    }
    catch
    {
      // The comparer runs while only the current node is locked.
      node?.WriteUnlock();
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
          if (child.IsFull)
          {
            UnlockFilledSplitChild(node, child);
            node = null;
            return AddOrUpdate(in key, adder, updater, out opIndex);
          }
        }
        node.WriteUnlock();
        node = child;
      }
    }
    catch
    {
      // The comparer and the callbacks run while only the current node is locked.
      node?.WriteUnlock();
      throw;
    }
  }
}
