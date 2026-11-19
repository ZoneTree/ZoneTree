using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;

namespace ZoneTree.Compression;

/// <summary>
/// Finds the uses of LZ4, Gzip and Brotli compression, which version 2.2.0 removes.
/// </summary>
static class LegacyCompression
{
  public static bool IsLegacy(CompressionMethod method) =>
      method != CompressionMethod.Zstd && method != CompressionMethod.None;

  /// <summary>
  /// Returns the compression options that select a legacy method.
  /// </summary>
  public static List<string> FindInOptions(WriteAheadLogOptions wal, DiskSegmentOptions disk)
  {
    var uses = new List<string>();
    if (IsLegacy(wal.CompressionMethod))
      uses.Add($"{nameof(WriteAheadLogOptions)}.{nameof(WriteAheadLogOptions.CompressionMethod)} = {wal.CompressionMethod}");
    if (IsLegacy(disk.CompressionMethod))
      uses.Add($"{nameof(DiskSegmentOptions)}.{nameof(DiskSegmentOptions.CompressionMethod)} = {disk.CompressionMethod}");
    return uses;
  }

  /// <summary>
  /// Throws when the compression options select a legacy method.
  /// </summary>
  public static void ThrowIfSelected(WriteAheadLogOptions wal, DiskSegmentOptions disk, string rule)
  {
    if (IsLegacy(wal.CompressionMethod))
      throw new InvalidOptionValueException(
          $"{nameof(WriteAheadLogOptions)}.{nameof(WriteAheadLogOptions.CompressionMethod)}",
          wal.CompressionMethod,
          rule);
    if (IsLegacy(disk.CompressionMethod))
      throw new InvalidOptionValueException(
          $"{nameof(DiskSegmentOptions)}.{nameof(DiskSegmentOptions.CompressionMethod)}",
          disk.CompressionMethod,
          rule);
  }

  public static void Warn(ILogger logger, IEnumerable<string> uses)
  {
    logger?.LogWarning(
        "ZoneTree 2.2.0 removes LZ4, Gzip and Brotli compression, and opens only the databases " +
        "that ZoneTree 2.1.10 found free of them. Migrate this database to Zstd with ZoneTree 2.1.10 " +
        "before upgrading. It uses them in: " + string.Join(", ", uses));
  }
}
