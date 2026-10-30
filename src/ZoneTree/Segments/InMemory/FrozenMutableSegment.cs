using ZoneTree.Collections;
using ZoneTree.Collections.BTree;
using ZoneTree.Core;
using ZoneTree.Exceptions;
using ZoneTree.Hashers;

namespace ZoneTree.Segments.InMemory;

public sealed class FrozenMutableSegment<TKey, TValue> : IMutableSegment<TKey, TValue>
{
  private IMutableSegment<TKey, TValue> mutableSegment;

  /// <summary>
  /// When set, writes throw instead of waiting for the next mutable segment.
  /// </summary>
  readonly Exception WriteFailure;

  public FrozenMutableSegment(
      IMutableSegment<TKey, TValue> mutableSegment,
      Exception writeFailure = null)
  {
    this.mutableSegment = mutableSegment;
    WriteFailure = writeFailure;
  }

  AddOrUpdateResult RetryOrThrow(out long opIndex)
  {
    if (WriteFailure != null)
      throw new WriteOrderBarrierFailedException(WriteFailure);
    opIndex = 0;
    return AddOrUpdateResult.RETRY_SEGMENT_IS_FROZEN;
  }

  public bool IsFrozen => true;

  public IIncrementalIdProvider OpIndexProvider => mutableSegment.OpIndexProvider;

  public long SegmentId => mutableSegment.SegmentId;

  public long Length => mutableSegment.Length;

  public long MaximumOpIndex => mutableSegment.MaximumOpIndex;

  public bool IsFullyFrozen => false;

  public bool ContainsKey(in TKey key, ref KeyHashProvider<TKey> keyHashProvider)
  {
    return mutableSegment.ContainsKey(key, ref keyHashProvider);
  }

  public AddOrUpdateResult Delete(in TKey key, out long opIndex)
  {
    return RetryOrThrow(out opIndex);
  }

  public void Drop()
  {
    mutableSegment.Drop();
  }

  public void Freeze()
  {
  }

  public Task SyncWALAsync()
  {
    return mutableSegment.SyncWALAsync();
  }

  public IIndexedReader<TKey, TValue> GetIndexedReader()
  {
    throw new NotSupportedException("BTree Indexed Reader is not supported.");
  }

  public ISeekableIterator<TKey, TValue> GetSeekableIterator(bool contributeToTheBlockCache = false)
  {
    return mutableSegment.GetSeekableIterator(contributeToTheBlockCache);
  }

  public void ReleaseResources()
  {
    mutableSegment.ReleaseResources();
  }

  public bool TryGet(in TKey key, out TValue value, ref KeyHashProvider<TKey> keyHashProvider)
  {
    return mutableSegment.TryGet(key, out value, ref keyHashProvider);
  }

  public AddOrUpdateResult Upsert(in TKey key, in TValue value, out long opIndex)
  {
    return RetryOrThrow(out opIndex);
  }

  public AddOrUpdateResult Upsert(in TKey key, GetValueDelegate<TKey, TValue> valueGetter, out long opIndex)
  {
    return RetryOrThrow(out opIndex);
  }
}
