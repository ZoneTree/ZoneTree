namespace ZoneTree.Transactional.Mvcc;

/// <summary>
/// The status of a transaction that can own versions in the storage tree.
/// Readers and merges look it up by the version of a record.
/// </summary>
/// <remarks>
/// A missing entry means the versions are committed and visible to every snapshot,
/// unless the storage tree deleted them for a rollback.
/// Entries of committed transactions are removed once every active snapshot sees them.
/// Entries of rolled back transactions are removed once no transaction that began before
/// the deletions of their versions is active. A rollback whose versions were not all deleted,
/// because of a crash or a storage failure, stays registered for good.
/// </remarks>
internal sealed class TransactionEntry
{
  int state;

  public long Id { get; }

  /// <summary>
  /// Written before <see cref="State"/> becomes committed.
  /// </summary>
  public long CommitTimestamp { get; set; }

  /// <summary>
  /// A mutable segment id at or after the segment that holds the commit record.
  /// The commit is durable once that segment is merged into the disk segment.
  /// Written before <see cref="State"/> becomes committed.
  /// </summary>
  public long CommitSegmentId { get; set; }

  public TransactionState State
  {
    get => (TransactionState)Volatile.Read(ref state);
    set => Volatile.Write(ref state, (int)value);
  }

  /// <summary>
  /// The clock after the rollback deleted the versions.
  /// </summary>
  public long AbortSequence { get; set; }

  public TransactionEntry(long id, TransactionState state)
  {
    Id = id;
    this.state = (int)state;
  }
}
