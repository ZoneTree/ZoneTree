using ZoneTree.Exceptions.WAL;

namespace ZoneTree.WAL;

public interface IWriteAheadLog<TKey, TValue> : IDisposable
{
  string FilePath { get; }

  bool EnableIncrementalBackup { get; set; }

  /// <summary>
  /// The initial record count of the log.
  /// It is available after the ReadLogEntries call.
  /// </summary>
  int InitialLength { get; }

  /// <summary>
  /// Appends a key/value write with the operation index assigned by the
  /// mutable segment.
  /// </summary>
  /// <param name="key">The key of the record.</param>
  /// <param name="value">The value of the record.</param>
  /// <param name="opIndex">
  /// The producer write sequence assigned to the record.
  /// </param>
  void Append(in TKey key, in TValue value, long opIndex);

  /// <summary>
  /// Syncs the records appended before this call to the disk.
  /// </summary>
  /// <remarks>
  /// Concurrent calls can share one disk sync.
  /// A frozen, disposed or dropped write ahead log completes the task
  /// when its final writes are synced.
  /// After a write or a disk sync fails, later calls fail too,
  /// because the operating system may have dropped records that
  /// were not synced yet.
  /// </remarks>
  /// <returns>
  /// A task that completes when the records are durable. It faults with
  /// <see cref="ZoneTree.Exceptions.WriteAheadLogSyncFailedException"/>
  /// when the records cannot be made durable.
  /// </returns>
  Task SyncAsync();

  void Drop();

  WriteAheadLogReadLogEntriesResult<TKey, TValue> ReadLogEntries(
      bool stopReadOnException,
      bool stopReadOnChecksumFailure,
      bool sortByOpIndexes);

  /// <summary>
  /// Replaces the entire write ahead log,
  /// with given keys and values.
  /// If enabled, appends current wal data to the incremental backup log.
  /// </summary>
  /// <remarks>
  /// Replacement writes a compacted state snapshot. Implementations are not
  /// required to preserve the exact historical operation index of each
  /// discarded WAL record. The owning tree must persist the operation-index
  /// high-water mark before replacement can remove the WAL evidence of it.
  /// </remarks>
  /// <param name="keys">new keys</param>
  /// <param name="values">new values</param>
  /// <param name="disableBackup">disable backup regardless of wal flag.</param>
  /// <returns>the difference: old file length - new file length.</returns>
  long ReplaceWriteAheadLog(TKey[] keys, TValue[] values, bool disableBackup);

  /// <summary>
  /// Informs the write ahead log that no further writes will be sent.
  /// to let WAL optimize itself.
  /// </summary>
  void MarkFrozen();

  /// <summary>
  /// Truncates incomplete tail record.
  /// </summary>
  /// <param name="incompleteTailException"></param>
  void TruncateIncompleteTailRecord(IncompleteTailRecordFoundException incompleteTailException);
}
