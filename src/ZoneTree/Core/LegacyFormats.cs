using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;

namespace ZoneTree.Core;

/// <summary>
/// Finds the uses of LZ4, Gzip and Brotli compression and of the Sync WAL mode,
/// which version 2.2.0 removes.
/// </summary>
static class LegacyFormats
{
  const string WalModeOption = $"{nameof(WriteAheadLogOptions)}.{nameof(WriteAheadLogOptions.WriteAheadLogMode)}";

  const string WalCompressionOption = $"{nameof(WriteAheadLogOptions)}.{nameof(WriteAheadLogOptions.CompressionMethod)}";

  const string DiskCompressionOption = $"{nameof(DiskSegmentOptions)}.{nameof(DiskSegmentOptions.CompressionMethod)}";

  public static bool IsLegacy(CompressionMethod method) =>
      method != CompressionMethod.Zstd && method != CompressionMethod.None;

  /// <summary>
  /// Returns whether the WAL compresses with a legacy method. Only compressed WAL modes use the method.
  /// </summary>
  static bool CompressesWithLegacyMethod(WriteAheadLogOptions wal) =>
      (wal.WriteAheadLogMode is WriteAheadLogMode.SyncCompressed or WriteAheadLogMode.AsyncCompressed
          or WriteAheadLogMode.Shared or WriteAheadLogMode.AsyncShared) &&
      IsLegacy(wal.CompressionMethod);

  /// <summary>
  /// Returns the options that select a legacy format.
  /// </summary>
  public static List<string> FindInOptions(WriteAheadLogOptions wal, DiskSegmentOptions disk)
  {
    var uses = new List<string>();
    if (wal.WriteAheadLogMode == WriteAheadLogMode.Sync)
      uses.Add($"{WalModeOption} = {wal.WriteAheadLogMode}");
    if (CompressesWithLegacyMethod(wal))
      uses.Add($"{WalCompressionOption} = {wal.CompressionMethod}");
    if (IsLegacy(disk.CompressionMethod))
      uses.Add($"{DiskCompressionOption} = {disk.CompressionMethod}");
    return uses;
  }

  const string SupportedWalModes = "Set it to SyncCompressed or another WAL mode";

  const string SupportedCompressionMethods = "Set it to Zstd or None";

  /// <summary>
  /// Throws when the options select a legacy format. The message tells the value to set,
  /// and then the reason.
  /// </summary>
  public static void ThrowIfSelected(WriteAheadLogOptions wal, DiskSegmentOptions disk, string reason)
  {
    if (wal.WriteAheadLogMode == WriteAheadLogMode.Sync)
      throw new InvalidOptionValueException(
          WalModeOption, wal.WriteAheadLogMode, $"{SupportedWalModes}. {reason}");
    if (CompressesWithLegacyMethod(wal))
      throw new InvalidOptionValueException(
          WalCompressionOption, wal.CompressionMethod, $"{SupportedCompressionMethods}. {reason}");
    if (IsLegacy(disk.CompressionMethod))
      throw new InvalidOptionValueException(
          DiskCompressionOption, disk.CompressionMethod, $"{SupportedCompressionMethods}. {reason}");
  }

  /// <summary>
  /// Throws when the WAL compression method is legacy, even for a WAL mode that does not use it.
  /// </summary>
  public static void ThrowIfLegacyWalCompression(WriteAheadLogOptions wal, string reason)
  {
    if (IsLegacy(wal.CompressionMethod))
      throw new InvalidOptionValueException(
          WalCompressionOption, wal.CompressionMethod, $"{SupportedCompressionMethods}. {reason}");
  }

  public static void Warn(ILogger logger, IEnumerable<string> uses)
  {
    logger?.LogWarning(
        "This database uses LZ4, Gzip or Brotli compression or the Sync WAL mode, which ZoneTree 2.2.0 " +
        "removes: " + string.Join(", ", uses) + ". Before you upgrade to ZoneTree 2.2.0, set Zstd or None " +
        "compression and a WAL mode other than Sync, and open the database once with " +
        "ZoneTreeFactory.EnableZstdMigration().");
  }
}
