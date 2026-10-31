using ZoneTree.Collections.BTree.Lock;

namespace ZoneTree.Collections.BTree;

public sealed partial class BTree<TKey, TValue>
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

    public NodeIterator GetIterator(BTree<TKey, TValue> tree)
    {
      try
      {
        ReadLock();
        var keys = new TKey[Length];
        var values = new TValue[Length];
        Array.Copy(Keys, 0, keys, 0, Length);
        Array.Copy(Values, 0, values, 0, Length);
        return new NodeIterator(tree, this, keys, values);
      }
      finally
      {
        ReadUnlock();
      }
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
