using ZoneTree.Collections.BPlusTreeLocks;

namespace ZoneTree.Collections;

public sealed partial class BPlusTree<TKey, TValue>
{
  public sealed class LeafNode : Node
  {
    public TValue[] Values;

    public volatile LeafNode Previous;

    public volatile LeafNode Next;

    public LeafNode(ILocker locker, int leafSize) : base(locker)
    {
      Keys = new TKey[leafSize];
      Values = new TValue[leafSize];
    }

    public LeafNode(ILocker locker, int length, TKey[] keys, TValue[] values)
        : base(locker)
    {
      Length = length;
      Keys = keys;
      Values = values;
    }

    public void Update(int position, in TKey key, in TValue value)
    {
      BeginChange();
      Keys[position] = key;
      Values[position] = value;
      EndChange();
    }

    public void UpdateValue(int position, in TValue value)
    {
      BeginChange();
      Values[position] = value;
      EndChange();
    }

    public void Insert(int position, in TKey key, in TValue value)
    {
      BeginChange();
      var len = Length - position;
      if (len > 0)
      {
        Array.Copy(Keys, position, Keys, position + 1, len);
        Array.Copy(Values, position, Values, position + 1, len);
      }
      Keys[position] = key;
      Values[position] = value;
      ++Length;
      EndChange();
    }

    /// <summary>
    /// Returns an iterator over a copy of the leaf. The leaf is copied without locking it, as reads find records, so
    /// iterators created at the same time do not queue on the leaf's lock. A copy that a concurrent change may have
    /// torn is discarded. Only a leaf that writers keep changing is copied under its lock.
    /// </summary>
    public NodeIterator GetIterator(BPlusTree<TKey, TValue> tree)
    {
      var spinWait = new SpinWait();
      for (var i = 0; i < MaximumOptimisticReadCount; ++i)
      {
        var version = Version;
        if ((version & 1) != 0)
        {
          spinWait.SpinOnce();
          continue;
        }
        var (keys, values) = CopyRecords();
        if (IsUnchangedSince(version))
          return new NodeIterator(tree, this, keys, values);
      }

      try
      {
        ReadLock();
        var (keys, values) = CopyRecords();
        return new NodeIterator(tree, this, keys, values);
      }
      finally
      {
        ReadUnlock();
      }
    }

    /// <summary>
    /// Copies the leaf's records. An empty leaf, the usual state of a settled tree's mutable segment, shares empty
    /// arrays, because iterators never write into them.
    /// </summary>
    (TKey[] Keys, TValue[] Values) CopyRecords()
    {
      var length = Length;
      if (length == 0)
        return (Array.Empty<TKey>(), Array.Empty<TValue>());
      var keys = new TKey[length];
      var values = new TValue[length];
      Array.Copy(Keys, 0, keys, 0, length);
      Array.Copy(Values, 0, values, 0, length);
      return (keys, values);
    }

    public FrozenNodeIterator CreateFrozenIterator()
    {
      return new FrozenNodeIterator(this);
    }

    public (LeafNode left, LeafNode right) SplitLeaf(
        int middle,
        int leafSize, ILocker locker1, ILocker locker2)
    {
      var left = new LeafNode(locker1, leafSize);
      var right = new LeafNode(locker2, leafSize);
      left.Length = middle;
      right.Length = Length - middle;
      Array.Copy(Keys, 0, left.Keys, 0, middle);
      Array.Copy(Values, 0, left.Values, 0, middle);
      Array.Copy(Keys, middle, right.Keys, 0, right.Length);
      Array.Copy(Values, middle, right.Values, 0, right.Length);

      left.Next = right;
      right.Previous = left;

      return (left, right);
    }

    public override Node CloneWithNoLock()
    {
      return new LeafNode(NoLock.Instance, Length, Keys, Values);
    }
  }
}
