namespace ZoneTree.Collections.BTree;

public sealed class FrozenBTreeSeekableIterator<TKey, TValue>
    : ISeekableIterator<TKey, TValue>
{
  public TKey CurrentKey =>
      HasCurrent ?
      CurrentNode.CurrentKey :
      throw new IndexOutOfRangeException(
          "Iterator is not in a valid position. Have you forgotten to call Next() or Prev()?");

  public TValue CurrentValue =>
      HasCurrent ?
      CurrentNode.CurrentValue :
      throw new IndexOutOfRangeException(
          "Iterator is not in a valid position. Did you forget to call Next() or Prev()?");

  public bool HasCurrent => CurrentNode != null && CurrentNode.HasCurrent;

  public bool IsBeginningOfAPart => false;

  public bool IsEndOfAPart => false;

  public bool IsFullyFrozen => BTree.IsReadOnly;

  readonly BTree<TKey, TValue> BTree;

  BTree<TKey, TValue>.FrozenNodeIterator CurrentNode;

  // The first leaf is opened on first use. Iterators are usually positioned
  // by a seek, so opening it up front would be wasted work.
  bool IsPositioned;

  public FrozenBTreeSeekableIterator(BTree<TKey, TValue> bTree)
  {
    BTree = bTree;
  }

  void EnsurePositioned()
  {
    if (IsPositioned)
      return;
    CurrentNode = BTree.GetFrozenFirstIterator();
    IsPositioned = true;
  }

  public bool Next()
  {
    EnsurePositioned();
    if (CurrentNode.Next())
      return true;

    var nextNode = CurrentNode.GetNextNodeIterator();
    if (nextNode == null)
      return false;
    nextNode.SeekBegin();
    CurrentNode = nextNode;
    return nextNode.HasCurrent;
  }

  public bool Prev()
  {
    EnsurePositioned();
    if (CurrentNode.Previous())
      return true;

    var prevNode = CurrentNode.GetPreviousNodeIterator();
    if (prevNode == null)
      return false;
    CurrentNode = prevNode;
    prevNode.SeekEnd();
    return prevNode.HasCurrent;
  }

  public bool SeekBegin()
  {
    IsPositioned = true;
    CurrentNode = BTree.GetFrozenFirstIterator();
    CurrentNode.SeekBegin();
    return HasCurrent;
  }

  public bool SeekEnd()
  {
    IsPositioned = true;
    CurrentNode = BTree.GetFrozenLastIterator();
    CurrentNode.SeekEnd();
    return HasCurrent;
  }

  public bool SeekToFirstGreaterOrEqualElement(in TKey key)
  {
    IsPositioned = true;
    CurrentNode = BTree.GetFrozenIteratorWithFirstKeyGreaterOrEqual(in key);
    return HasCurrent;
  }

  public bool SeekToLastSmallerOrEqualElement(in TKey key)
  {
    IsPositioned = true;
    CurrentNode = BTree.GetFrozenIteratorWithLastKeySmallerOrEqual(in key);
    return HasCurrent;
  }

  public void Skip(long offset)
  {
    throw new NotSupportedException();
  }

  public int GetPartIndex() => -1;
}
