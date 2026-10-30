using ZoneTree.Exceptions;

namespace ZoneTree.Collections.BTree;

public delegate TValue GetValueDelegate<TKey, TValue>(long opIndex);

/// <summary>
/// In memory B+Tree.
/// This class is thread-safe.
/// </summary>
/// <typeparam name="TKey">Key Type</typeparam>
/// <typeparam name="TValue">Value Type</typeparam>
public sealed partial class BTree<TKey, TValue>
{
  public bool Upsert(in TKey key, GetValueDelegate<TKey, TValue> valueGetter, out TValue value, out long opIndex)
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
        return UpsertNonFull(root, in key, valueGetter, out value, out opIndex);
      }
    }
    finally
    {
      WriteUnlock();
    }
  }

  bool UpsertNonFull(Node node, in TKey key, GetValueDelegate<TKey, TValue> valueGetter, out TValue value, out long opIndex)
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
            value = valueGetter(opIndex);
            leaf.Update(position, in key, value);
            node.WriteUnlock();
            return false;
          }
          value = valueGetter(opIndex);
          leaf.Insert(position, in key, value);
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
      // The comparer and the value getter run while only the current node is locked.
      node.WriteUnlock();
      throw;
    }
  }
}
