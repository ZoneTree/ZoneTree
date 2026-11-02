using ZoneTree.Options;

namespace ZoneTree.WAL;

public sealed class SyncCompressedModeOptions
{
  /// <summary>
  /// In sync-compressed WAL mode, if enabled
  /// a separate thread syncs the complete dirty WAL to the disk periodically.
  /// This limits what a power loss can lose. Process crashes do not depend on it.
  /// Default value is true.
  /// </summary>
  public bool EnableTailWriterJob { get; set; } = WriteAheadLogDefaultValues.SyncCompressedModeEnableTailWriterJob;

  /// <summary>
  /// The delay in milliseconds before the next background WAL sync.
  /// Default value is 500 ms.
  /// </summary>
  public int TailWriterJobInterval { get; set; } = WriteAheadLogDefaultValues.SyncCompressedModeTailWriterJobInterval;
}
