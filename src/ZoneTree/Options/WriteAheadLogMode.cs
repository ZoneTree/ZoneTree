namespace ZoneTree.Options;

/// <summary>
/// Available write ahead log modes.
/// </summary>
public enum WriteAheadLogMode : byte
{
  /// <summary>
  /// Sync mode write ahead log writes each record through the plain WAL stream
  /// before the ZoneTree write is considered complete.
  /// It favors simple synchronous WAL recovery semantics over write speed.
  /// Full power-loss durability still depends on the
  /// operating system, file system, and storage device.
  /// </summary>
  Sync = 0,

  /// <summary>
  /// Sync mode with compressed-block WAL storage.
  /// It is faster than Sync and can reduce WAL size. It keeps the current
  /// compressed tail block in a separate file, and every write to that block
  /// survives a process crash. A power loss can lose the writes that were not
  /// synced to the disk.
  /// </summary>
  SyncCompressed = 1,

  /// <summary>
  /// AsyncCompressed mode writes compressed WAL records through a background
  /// path. It is the recommended high-throughput persistent default for most
  /// applications. A process crash loses the writes that are still queued for
  /// the background path, and a power loss can also lose the appended writes
  /// that were not synced to the disk.
  /// </summary>
  AsyncCompressed = 2,

  /// <summary>
  /// No Write Ahead Log. Nothing is saved to the WAL file.
  /// Inserts stay in memory. Data in memory can disappear on
  /// process crashes / terminations or power cuts.
  /// It is still possible to save in memory data to the disk segments
  /// using MoveMutableSegmentForward and StartMergeOperation
  /// methods in IZoneTreeMaintenance.
  /// </summary>
  None = 3,
}
