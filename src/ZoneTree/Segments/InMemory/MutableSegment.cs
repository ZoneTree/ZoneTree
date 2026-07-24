using ZoneTree.Collections;
using ZoneTree.Collections.BTree;
using ZoneTree.Comparers;
using ZoneTree.Core;
using ZoneTree.Hashers;
using ZoneTree.Options;
using ZoneTree.WAL;

namespace ZoneTree.Segments.InMemory;

public sealed class MutableSegment<TKey, TValue> : IMutableSegment<TKey, TValue>
{
  readonly ZoneTreeOptions<TKey, TValue> Options;

  volatile bool IsFrozenFlag;

  volatile int WritesInProgress;

  readonly MarkValueDeletedDelegate<TValue> MarkValueDeleted;

  readonly int MutableSegmentMaxItemCount;

  readonly BTree<TKey, TValue> BTree;

  readonly ConcurrentBloomFilter BloomFilter;

  readonly IRefComparer<TKey> Comparer;

  readonly IWriteAheadLog<TKey, TValue> WriteAheadLog;

  public long SegmentId { get; private set; }

  public bool IsFrozen => IsFrozenFlag;

  public bool IsFullyFrozen => IsFrozenFlag && BTree.IsReadOnly;

  public long Length => BTree.Length;

  public long MaximumOpIndex => BTree.LastOpIndex;

  public IIncrementalIdProvider OpIndexProvider => BTree.OpIndexProvider;

  public MutableSegment(ZoneTreeOptions<TKey, TValue> options,
      long segmentId,
      IIncrementalIdProvider indexOpProvider)
  {
    SegmentId = segmentId;
    Options = options;
    WriteAheadLog = options.WriteAheadLogProvider
        .GetOrCreateWAL(
            SegmentId,
            ZoneTree<TKey, TValue>.SegmentWalCategory,
            options.WriteAheadLogOptions,
            options.KeySerializer,
            options.ValueSerializer);
    Comparer = options.Comparer;
    MutableSegmentMaxItemCount = options.MutableSegmentMaxItemCount;
    BloomFilter = CreateBloomFilter(options);

    BTree = new(Comparer,
        Options.BTreeLockMode,
        indexOpProvider,
        Options.BTreeNodeSize,
        Options.BTreeLeafSize);

    MarkValueDeleted = options.MarkValueDeleted;
  }

  public MutableSegment(
      long segmentId,
      IWriteAheadLog<TKey, TValue> wal,
      ZoneTreeOptions<TKey, TValue> options,
      IReadOnlyList<TKey> keys,
      IReadOnlyList<TValue> values,
      long nextOpIndex,
      bool collectGarbage)
  {
    SegmentId = segmentId;
    WriteAheadLog = wal;
    Options = options;
    Comparer = options.Comparer;
    MutableSegmentMaxItemCount = options.MutableSegmentMaxItemCount;
    BloomFilter = CreateBloomFilter(options);

    BTree = new(
        Comparer,
        Options.BTreeLockMode,
        null,
        Options.BTreeNodeSize,
        Options.BTreeLeafSize);

    MarkValueDeleted = options.MarkValueDeleted;
    if (collectGarbage)
    {
      // If there isn't any disk segment and readonly segment,
      // it is safe to hard delete the soft deleted values.
      LoadLogEntriesWithGarbageCollection(keys, values);
    }
    else
    {
      LoadLogEntries(keys, values);
    }
    // set op index after loading entries.
    BTree.SetNextOpIndex(nextOpIndex);
  }

  void LoadLogEntries(IReadOnlyList<TKey> keys, IReadOnlyList<TValue> values)
  {
    var len = keys.Count;
    for (var i = 0; i < len; ++i)
    {
      var key = keys[i];
      var value = values[i];
      // TODO: Search if we can create faster construction
      // of mutable segment from log entries.
      AddToBloomFilter(in key);
      BTree.Upsert(in key, in value, out var _);
    }
  }

  void LoadLogEntriesWithGarbageCollection(
      IReadOnlyList<TKey> keys,
      IReadOnlyList<TValue> values)
  {
    var distinctKeys =
        new BTree<TKey, byte>(Options.Comparer, Collections.BTree.Lock.BTreeLockMode.NoLock);

    var isDeleted = Options.IsDeleted;
    for (var i = keys.Count - 1; i >= 0; --i)
    {
      var key = keys[i];
      if (distinctKeys.ContainsKey(in key))
        continue;
      var value = values[i];
      distinctKeys.Upsert(in key, 1, out _);
      if (isDeleted(in key, in value))
      {
        continue;
      }
      AddToBloomFilter(in key);
      BTree.Upsert(in key, in value, out var _);
    }
  }

  public bool ContainsKey(in TKey key, ref KeyHashProvider<TKey> keyHashProvider)
  {
    if (BTree.Length == 0 || BloomFilter != null &&
        !BloomFilter.MightContain(keyHashProvider.GetHashCode(in key, Options.KeyHasher)))
      return false;
    return BTree.ContainsKey(key);
  }

  public bool TryGet(in TKey key, out TValue value, ref KeyHashProvider<TKey> keyHashProvider)
  {
    if (BTree.Length == 0 ||
        (BloomFilter != null &&
         !BloomFilter.MightContain(keyHashProvider.GetHashCode(in key, Options.KeyHasher))))
    {
      value = default;
      return false;
    }
    return BTree.TryGetValue(key, out value);
  }

  public AddOrUpdateResult Upsert(in TKey key, in TValue value, out long opIndex)
  {
    try
    {
      Interlocked.Increment(ref WritesInProgress);

      if (IsFrozenFlag)
      {
        opIndex = 0;
        return AddOrUpdateResult.RETRY_SEGMENT_IS_FROZEN;
      }

      if (BTree.Length >= MutableSegmentMaxItemCount)
      {
        opIndex = 0;
        return AddOrUpdateResult.RETRY_SEGMENT_IS_FULL;
      }
      AddToBloomFilter(in key);
      var result = BTree.Upsert(in key, in value, out opIndex);
      WriteAheadLog.Append(in key, in value, opIndex);
      return result ? AddOrUpdateResult.ADDED : AddOrUpdateResult.UPDATED;
    }
    finally
    {
      Interlocked.Decrement(ref WritesInProgress);
    }
  }

  public AddOrUpdateResult Upsert(
      in TKey key,
      GetValueDelegate<TKey, TValue> valueGetter,
      out long opIndex)
  {
    try
    {
      Interlocked.Increment(ref WritesInProgress);

      if (IsFrozenFlag)
      {
        opIndex = 0;
        return AddOrUpdateResult.RETRY_SEGMENT_IS_FROZEN;
      }

      if (BTree.Length >= MutableSegmentMaxItemCount)
      {
        opIndex = 0;
        return AddOrUpdateResult.RETRY_SEGMENT_IS_FULL;
      }
      AddToBloomFilter(in key);
      var result = BTree.Upsert(in key, valueGetter, out var value, out opIndex);
      WriteAheadLog.Append(in key, in value, opIndex);
      return result ? AddOrUpdateResult.ADDED : AddOrUpdateResult.UPDATED;
    }
    finally
    {
      Interlocked.Decrement(ref WritesInProgress);
    }
  }

  public AddOrUpdateResult Delete(in TKey key, out long opIndex)
  {
    try
    {
      Interlocked.Increment(ref WritesInProgress);

      if (IsFrozenFlag)
      {
        opIndex = 0;
        return AddOrUpdateResult.RETRY_SEGMENT_IS_FROZEN;
      }

      if (BTree.Length >= MutableSegmentMaxItemCount)
      {
        opIndex = 0;
        return AddOrUpdateResult.RETRY_SEGMENT_IS_FULL;
      }

      TValue insertedValue = default;

      AddToBloomFilter(in key);
      var status = BTree
          .AddOrUpdate(key,
              void (ref TValue x) =>
              {
                MarkValueDeleted(ref x);
                insertedValue = x;
              },
              void (ref TValue x) =>
              {
                MarkValueDeleted(ref x);
                insertedValue = x;
              }, out opIndex);
      WriteAheadLog.Append(in key, in insertedValue, opIndex);
      return status;
    }
    finally
    {
      Interlocked.Decrement(ref WritesInProgress);
    }
  }

  public void Freeze()
  {
    IsFrozenFlag = true;
    new Thread(FreezeWriteAheadLog).Start();
  }

  ConcurrentBloomFilter CreateBloomFilter(
      ZoneTreeOptions<TKey, TValue> options)
  {
    if (options.KeyHasher == null)
      return null;
    var bitsPerItem = options.MutableSegmentBloomFilterBitsPerItem;
    return bitsPerItem == 0 ?
        null :
        new ConcurrentBloomFilter(
            MutableSegmentMaxItemCount,
            bitsPerItem);
  }

  void AddToBloomFilter(in TKey key)
  {
    BloomFilter?.Add(Options.KeyHasher.GetHashCode(in key));
  }

  void FreezeWriteAheadLog()
  {
    try
    {
      while (WritesInProgress > 0)
      {
        Thread.Yield();
      }
      WriteAheadLog.MarkFrozen();
      BTree.SetTreeReadOnlyAndLockFree();

    }
    catch (Exception e)
    {
      Options.Logger.LogError(e);
    }
  }

  public void Drop()
  {
    Options.WriteAheadLogProvider.RemoveWAL(SegmentId, ZoneTree<TKey, TValue>.SegmentWalCategory);
    WriteAheadLog?.Drop();
  }

  public IIndexedReader<TKey, TValue> GetIndexedReader()
  {
    throw new NotSupportedException("BTree Indexed Reader is not supported.");
  }

  public ISeekableIterator<TKey, TValue> GetSeekableIterator(bool contributeToTheBlockCache)
  {
    return IsFullyFrozen ?
        new FrozenBTreeSeekableIterator<TKey, TValue>(BTree) :
        new BTreeSeekableIterator<TKey, TValue>(BTree);
  }

  public void ReleaseResources()
  {
    WriteAheadLog?.MarkFrozen();
    Options.WriteAheadLogProvider.RemoveWAL(SegmentId, ZoneTree<TKey, TValue>.SegmentWalCategory);
    WriteAheadLog?.Dispose();
  }
}
