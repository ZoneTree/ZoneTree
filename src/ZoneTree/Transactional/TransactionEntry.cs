namespace ZoneTree.Transactional;

/// <summary>
/// The status of a transaction that can own versions in the storage tree.
/// Readers and merges look it up by the version of a record.
/// </summary>
/// <remarks>
/// A missing entry means the versions are committed and visible to every snapshot.
/// Entries of committed transactions are removed once every active snapshot sees them.
/// Entries of rolled back transactions stay until their versions cannot be read anymore.
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
  /// The highest bottom segment id at the first write.
  /// </summary>
  public long BottomMarker { get; set; }

  /// <summary>
  /// The mutable segment id after the rollback deleted the versions.
  /// </summary>
  public long AbortSegmentId { get; set; }

  /// <summary>
  /// The clock after the rollback deleted the versions.
  /// </summary>
  public long AbortSequence { get; set; }

  /// <summary>
  /// True if the versions may outlive the deletions, so the entry is never removed.
  /// </summary>
  public bool IsPermanent { get; set; }

  public TransactionEntry(long id, TransactionState state)
  {
    Id = id;
    this.state = (int)state;
  }
}
