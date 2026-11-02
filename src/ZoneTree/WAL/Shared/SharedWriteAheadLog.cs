using ZoneTree.Collections.TimSort;
using ZoneTree.Exceptions.WAL;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

/// <summary>
/// A segment handle in the tree's ordered, chunked journal. Handles share
/// disk syncs and keep memory-segment rotation independent of disk latency.
/// Select Shared or AsyncShared through WriteAheadLogOptions.
/// </summary>
public sealed class SharedWriteAheadLog<TKey, TValue> : IWriteAheadLog<TKey, TValue>, ISharedWriteAheadLogCoordinator
{
  readonly SharedWalStore<TKey, TValue> Store;
  readonly long SegmentId;
  readonly ISerializer<TKey> Keys;
  readonly ISerializer<TValue> Values;
  readonly Lock LifecycleLock = new();
  volatile bool Frozen;
  volatile bool Closed;
  Task FinalSync;

  internal SharedWriteAheadLog(SharedWalStore<TKey, TValue> store, long segmentId,
      ISerializer<TKey> keys, ISerializer<TValue> values)
  {
    Store = store;
    SegmentId = segmentId;
    Keys = keys;
    Values = values;
    store.Register(segmentId);
  }

  public string FilePath => Store.DirectoryPath;
  public int InitialLength { get; private set; }

  public void Append(in TKey key, in TValue value, long opIndex)
  {
    if (Closed || Frozen) throw new InvalidOperationException("The shared WAL handle is frozen or closed.");
    Store.Append(SegmentId, key, value, opIndex);
  }

  public Task SyncAsync()
  {
    lock (LifecycleLock)
      return Closed ? FinalSync : Store.RequestSync();
  }

  public void MarkFrozen() => Frozen = true;

  void ISharedWriteAheadLogCoordinator.SyncBeforeMerge() => Store.Sync();

  void ISharedWriteAheadLogCoordinator.CompleteRecovery(IReadOnlyCollection<long> liveSegments)
      => Store.CompleteRecovery(liveSegments);

  public WriteAheadLogReadLogEntriesResult<TKey, TValue> ReadLogEntries(
      bool stopReadOnException, bool stopReadOnChecksumFailure, bool sortByOpIndexes)
  {
    var records = Store.Read(SegmentId, out var maximum);
    var keys = new List<TKey>(records.Count);
    var values = new List<TValue>(records.Count);
    var indexes = new List<long>(records.Count);
    var result = new WriteAheadLogReadLogEntriesResult<TKey, TValue> { Success = true, MaximumOpIndex = maximum };
    for (var i = 0; i < records.Count; ++i)
      try
      {
        var key = Keys.Deserialize(records[i].Key);
        var value = Values.Deserialize(records[i].Value);
        keys.Add(key);
        values.Add(value);
        indexes.Add(records[i].OpIndex);
      }
      catch (Exception e)
      {
        result.Success = false;
        result.Exceptions.Add(i, new InvalidDataException("Cannot deserialize a shared WAL record.", e));
        if (stopReadOnException || stopReadOnChecksumFailure) break;
      }
    if (sortByOpIndexes)
    {
      var order = Enumerable.Range(0, keys.Count).ToArray();
      TimSort<int>.Sort(order, new WriteAheadLogEntryReader.LogEntryReaderComparer(indexes));
      result.Keys = order.Select(i => keys[i]).ToArray();
      result.Values = order.Select(i => values[i]).ToArray();
    }
    else { result.Keys = keys; result.Values = values; }
    InitialLength = keys.Count;
    return result;
  }

  public long ReplaceWriteAheadLog(TKey[] keys, TValue[] values)
  {
    var (size, maximum) = Store.GetLogicalState(SegmentId);
    return size - Store.Replace(SegmentId, keys, values, maximum);
  }

  public void TruncateIncompleteTailRecord(IncompleteTailRecordFoundException incompleteTailException)
  {
    // Physical suffix repair is performed once for the whole journal before
    // any handle is exposed. Never truncate another segment's records here.
    throw new InvalidOperationException("Shared WAL suffixes are repaired during journal recovery.");
  }

  public void Dispose() => Close(false);
  public void Drop() => Close(true);

  void Close(bool drop)
  {
    lock (LifecycleLock)
    {
      if (Closed) return;
      Closed = Frozen = true;
      Exception error = null;
      try
      {
        if (drop) Store.Retire(SegmentId);
        else
        {
          FinalSync = Store.RequestSync();
          FinalSync.GetAwaiter().GetResult();
        }
      }
      catch (Exception e) { error = e; }
      finally
      {
        try { Store.Release(); }
        catch (Exception e) { error ??= e; }
        FinalSync = error == null ? Task.CompletedTask : Task.FromException(error);
      }
      if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
  }
}
