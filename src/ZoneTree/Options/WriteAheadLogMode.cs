namespace ZoneTree.Options;

/// <summary>
/// Available write ahead log modes.
/// </summary>
/// <remarks>
/// A private WAL belongs to one memory segment, and each segment recovers its own records.
/// A shared WAL is one journal for all memory segments, and they recover one ordered prefix
/// of records. The caller writes the records in the Private and Shared modes, and a
/// background writer writes them in the AsyncPrivate and AsyncShared modes.
/// WAL file names hold the values.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1008", Justification = "WAL file names store the values.")]
public enum WriteAheadLogMode : byte
{
  /// <summary>
  /// Each memory segment has its own WAL, and the caller writes each record to it.
  /// The current block of the WAL is in a separate tail file, so every returned write
  /// survives a process crash. A power loss can lose the writes that were not synced
  /// to the disk.
  /// </summary>
  Private = 1,

  /// <summary>
  /// Each memory segment has its own WAL, and a background writer writes the records.
  /// It is the recommended high-throughput persistent default for most applications.
  /// A process crash loses the writes that are still queued for the writer, and a
  /// power loss can also lose the written records that were not synced to the disk.
  /// </summary>
  AsyncPrivate = 2,

  /// <summary>
  /// No Write Ahead Log. Nothing is saved to the WAL file.
  /// Inserts stay in memory. Data in memory can disappear on
  /// process crashes / terminations or power cuts.
  /// It is still possible to save in memory data to the disk segments
  /// using MoveMutableSegmentForward and StartMergeOperation
  /// methods in IZoneTreeMaintenance.
  /// </summary>
  None = 3,

  /// <summary>
  /// All memory segments share one ordered WAL journal, and the caller writes each
  /// record to it. The segments recover one ordered prefix of records. Segment
  /// rotation does not sync the journal. SyncWALAsync provides disk durability.
  /// </summary>
  Shared = 4,

  /// <summary>
  /// All memory segments share one ordered WAL journal, and a background writer
  /// writes the records. Written records have the same ordered recovery prefix as
  /// Shared; a process crash can lose queued records. SyncWALAsync drains the
  /// preceding writes and syncs the journal.
  /// </summary>
  AsyncShared = 5,
}
