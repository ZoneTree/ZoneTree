namespace ZoneTree.Collections;

public sealed class BPlusTreeSeekableIterator<TKey, TValue>
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

  public bool IsFullyFrozen => BPlusTree.IsReadOnly;

  readonly BPlusTree<TKey, TValue> BPlusTree;

  BPlusTree<TKey, TValue>.NodeIterator CurrentNode;

  // The first leaf is opened on first use. Iterators are usually positioned
  // by a seek, so opening it up front would be wasted work.
  bool IsPositioned;

  public BPlusTreeSeekableIterator(BPlusTree<TKey, TValue> bTree)
  {
    BPlusTree = bTree;
  }

  void EnsurePositioned()
  {
    if (IsPositioned)
      return;
    CurrentNode = BPlusTree.GetFirstIterator();
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
    prevNode.SeekEnd();
    CurrentNode = prevNode;
    return prevNode.HasCurrent;
  }

  public bool SeekBegin()
  {
    IsPositioned = true;
    CurrentNode = BPlusTree.GetFirstIterator();
    CurrentNode.SeekBegin();
    return HasCurrent;
  }

  public bool SeekEnd()
  {
    IsPositioned = true;
    CurrentNode = BPlusTree.GetLastIterator();
    CurrentNode.SeekEnd();
    return HasCurrent;
  }

  public bool SeekToFirstGreaterOrEqualElement(in TKey key)
  {
    IsPositioned = true;
    CurrentNode = BPlusTree.GetIteratorWithFirstKeyGreaterOrEqual(in key);
    return HasCurrent;
  }

  public bool SeekToLastSmallerOrEqualElement(in TKey key)
  {
    IsPositioned = true;
    CurrentNode = BPlusTree.GetIteratorWithLastKeySmallerOrEqual(in key);
    return HasCurrent;
  }

  public void Skip(long offset)
  {
    throw new NotSupportedException();
  }

  public int GetPartIndex() => -1;
}
