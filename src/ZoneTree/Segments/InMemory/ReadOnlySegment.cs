using ZoneTree.Collections;
using ZoneTree.Comparers;
using ZoneTree.Core;
using ZoneTree.Hashers;
using ZoneTree.Options;
using ZoneTree.Segments.Block;
using ZoneTree.WAL;

namespace ZoneTree.Segments.InMemory;

public sealed class ReadOnlySegment<TKey, TValue> : IReadOnlySegment<TKey, TValue>, IIndexedReader<TKey, TValue>
{
  public long SegmentId { get; }

  public long MaximumOpIndex { get; set; }

  readonly ZoneTreeOptions<TKey, TValue> Options;

  readonly IReadOnlyList<TKey> SortedKeys;

  readonly IReadOnlyList<TValue> SortedValues;

  readonly IRefComparer<TKey> Comparer;

  readonly IWriteAheadLog<TKey, TValue> WriteAheadLog;

  public long Length => SortedKeys.Count;

  public bool IsFullyFrozen => true;

  public bool HasNext => true;

  public bool HasPrev => true;

  public bool IsIterativeIndexReader => false;

  public ReadOnlySegment(
      long segmentId,
      ZoneTreeOptions<TKey, TValue> options,
      IReadOnlyList<TKey> sortedKeys,
      IReadOnlyList<TValue> sortedValues)
  {
    SegmentId = segmentId;
    Options = options;
    Comparer = options.Comparer;
    SortedKeys = sortedKeys;
    SortedValues = sortedValues;
    WriteAheadLog = options.WriteAheadLogProvider.GetWal<TKey, TValue>(
        SegmentId,
        ZoneTree<TKey, TValue>.SegmentWalCategory);
  }

  public bool TryGet(in TKey key, out TValue value, ref KeyHashProvider<TKey> keyHashProvider)
  {
    int index = BinarySearchAlgorithms
        .BinarySearch(SortedKeys, 0, SortedKeys.Count - 1, Comparer, in key);
    if (index < 0)
    {
      value = default;
      return false;
    }
    value = SortedValues[index];
    return true;
  }

  public bool ContainsKey(in TKey key, ref KeyHashProvider<TKey> keyHashProvider)
  {
    int index = BinarySearchAlgorithms
        .BinarySearch(SortedKeys, 0, SortedKeys.Count - 1, Comparer, in key);
    return index >= 0;
  }

  public TKey GetKey(long index)
  {
    return SortedKeys[(int)index];
  }

  public TValue GetValue(long index)
  {
    return SortedValues[(int)index];
  }

  public void Drop()
  {
    Options.WriteAheadLogProvider.RemoveWal(SegmentId, ZoneTree<TKey, TValue>.SegmentWalCategory);
    WriteAheadLog?.Drop();
  }

  public IIndexedReader<TKey, TValue> GetIndexedReader()
  {
    return this;
  }

  public ISeekableIterator<TKey, TValue> GetSeekableIterator(bool contributeToTheBlockCache)
  {
    return new SeekableIterator<TKey, TValue>(this, contributeToTheBlockCache);
  }

  /// <summary>
  /// Finds the position of element that is smaller or equal than key.
  /// </summary>
  /// <param name="key">The key</param>
  /// <returns>-1 or a valid position</returns>
  public long GetLastSmallerOrEqualPosition(in TKey key) =>
      BinarySearchAlgorithms
          .LastSmallerOrEqualPosition(SortedKeys, 0, SortedKeys.Count - 1, Comparer, in key);

  /// <summary>
  /// Finds the position of element that is greater or equal than key.
  /// </summary>
  /// <param name="key">The key</param>
  /// <returns>The length of the segment or a valid position</returns>
  public long GetFirstGreaterOrEqualPosition(in TKey key) =>
      BinarySearchAlgorithms
          .FirstGreaterOrEqualPosition(SortedKeys, 0, SortedKeys.Count - 1, Comparer, in key);

  public void ReleaseResources()
  {
    Options.WriteAheadLogProvider.RemoveWal(SegmentId, ZoneTree<TKey, TValue>.SegmentWalCategory);
    WriteAheadLog?.Dispose();
  }

  public bool IsBeginningOfAPart(long index) => false;

  public bool IsEndOfAPart(long index) => false;

  public int GetPartIndex(long index) => -1;

  public TKey GetKey(long index, BlockPin blockPin)
  {
    return SortedKeys[(int)index];
  }

  public TValue GetValue(long index, BlockPin blockPin)
  {
    return SortedValues[(int)index];
  }
}
