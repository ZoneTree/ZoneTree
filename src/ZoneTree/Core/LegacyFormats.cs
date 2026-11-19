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

  /// <summary>
  /// Throws when the options select a legacy format.
  /// </summary>
  public static void ThrowIfSelected(WriteAheadLogOptions wal, DiskSegmentOptions disk, string rule)
  {
    if (wal.WriteAheadLogMode == WriteAheadLogMode.Sync)
      throw new InvalidOptionValueException(WalModeOption, wal.WriteAheadLogMode, rule);
    if (CompressesWithLegacyMethod(wal))
      throw new InvalidOptionValueException(WalCompressionOption, wal.CompressionMethod, rule);
    if (IsLegacy(disk.CompressionMethod))
      throw new InvalidOptionValueException(DiskCompressionOption, disk.CompressionMethod, rule);
  }

  /// <summary>
  /// Throws when the WAL compression method is legacy, even for a WAL mode that does not use it.
  /// </summary>
  public static void ThrowIfLegacyWalCompression(WriteAheadLogOptions wal, string rule)
  {
    if (IsLegacy(wal.CompressionMethod))
      throw new InvalidOptionValueException(WalCompressionOption, wal.CompressionMethod, rule);
  }

  public static void Warn(ILogger logger, IEnumerable<string> uses)
  {
    logger?.LogWarning(
        "ZoneTree 2.2.0 removes LZ4, Gzip and Brotli compression and the Sync WAL mode, and opens only " +
        "the databases that ZoneTree 2.1.10 found free of them. Migrate this database with ZoneTree 2.1.10 " +
        "before upgrading. It uses them in: " + string.Join(", ", uses));
  }
}
