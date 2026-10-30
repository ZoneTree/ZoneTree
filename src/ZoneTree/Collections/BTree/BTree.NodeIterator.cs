using ZoneTree.Comparers;

namespace ZoneTree.Collections.BTree;

public sealed partial class BTree<TKey, TValue>
{
  public sealed class NodeIterator
  {
    public LeafNode Node { get; }

    public TKey[] Keys { get; }

    public TValue[] Values { get; }

    public TKey CurrentKey => Keys[CurrentIndex];

    public TValue CurrentValue => Values[CurrentIndex];

    public bool HasCurrent => CurrentIndex >= 0 && CurrentIndex < Keys.Length;

    int CurrentIndex = -1;

    readonly BTree<TKey, TValue> Tree;

    public NodeIterator(
        BTree<TKey, TValue> tree,
        LeafNode leafNode,
        TKey[] keys,
        TValue[] values)
    {
      Tree = tree;
      Node = leafNode;
      Keys = keys;
      Values = values;
    }

    public NodeIterator GetPreviousNodeIterator()
    {
      var topLevelLocker = Tree.TopLevelLocker;
      try
      {
        topLevelLocker.ReadLock();
        return Node.Previous?.GetIterator(Tree);
      }
      finally
      {
        topLevelLocker.ReadUnlock();
      }
    }

    public NodeIterator GetNextNodeIterator()
    {
      var topLevelLocker = Tree.TopLevelLocker;
      try
      {
        topLevelLocker.ReadLock();
        return Node.Next?.GetIterator(Tree);
      }
      finally
      {
        topLevelLocker.ReadUnlock();
      }
    }

    public bool HasNext()
    {
      return CurrentIndex + 1 < Keys.Length;
    }

    public bool Next()
    {
      if (!HasNext())
        return false;
      ++CurrentIndex;
      return CurrentIndex < Keys.Length;
    }

    public bool HasPrevious()
    {
      return CurrentIndex > 0;
    }

    public bool Previous()
    {
      if (!HasPrevious())
        return false;
      --CurrentIndex;
      return CurrentIndex >= 0;
    }

    public void SeekBegin()
    {
      CurrentIndex = 0;
    }

    public void SeekEnd()
    {
      CurrentIndex = Keys.Length - 1;
    }

    /// <summary>
    /// Positions at the last key smaller or equal than the key,
    /// or before the first key when there is none.
    /// </summary>
    public NodeIterator SeekLastKeySmallerOrEqual(
        IRefComparer<TKey> comparer, in TKey key)
    {
      var iterator = this;
      while (true)
      {
        var pos =
            iterator.GetLastSmallerOrEqualPosition(comparer, in key);
        if (pos == -1)
        {
          var previous = iterator.GetPreviousNodeIterator();
          if (previous != null)
          {
            iterator = previous;
            continue;
          }
        }
        iterator.CurrentIndex = pos;
        return iterator;
      }
    }

    /// <summary>
    /// Positions at the first key greater or equal than the key,
    /// or after the last key when there is none.
    /// </summary>
    public NodeIterator SeekFirstKeyGreaterOrEqual(
        IRefComparer<TKey> comparer, in TKey key)
    {
      var iterator = this;
      while (true)
      {
        var pos =
            iterator.GetFirstGreaterOrEqualPosition(comparer, in key);
        if (pos == iterator.Keys.Length)
        {
          var next = iterator.GetNextNodeIterator();
          if (next != null)
          {
            iterator = next;
            continue;
          }
        }
        iterator.CurrentIndex = pos;
        return iterator;
      }
    }

    /// <summary>
    /// Finds the position of element that is smaller or equal than key.
    /// </summary>
    /// <param name="comparer">The key comparer</param>
    /// <param name="key">The key</param>
    /// <returns>-1 or a valid position</returns>
    public int GetLastSmallerOrEqualPosition(
        IRefComparer<TKey> comparer, in TKey key)
        => BinarySearchAlgorithms
            .LastSmallerOrEqualPosition(Keys, 0, Keys.Length - 1, comparer, in key);

    /// <summary>
    /// Finds the position of element that is greater or equal than key.
    /// </summary>
    /// <param name="comparer">The key comparer</param>
    /// <param name="key">The key</param>
    /// <returns>The length of the segment or a valid position</returns>
    public int GetFirstGreaterOrEqualPosition(
        IRefComparer<TKey> comparer, in TKey key)
        => BinarySearchAlgorithms
            .FirstGreaterOrEqualPosition(Keys, 0, Keys.Length - 1, comparer, in key);
  }
}
