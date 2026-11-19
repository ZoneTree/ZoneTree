using System.Globalization;
using System.Text;
using ZoneTree.AbstractFileStream;
using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Segments.Disk;
using ZoneTree.Segments.MultiPart;
using ZoneTree.Segments.RandomAccess;
using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace ZoneTree.Core;

/// <summary>
/// Migrates the files of a database from LZ4, Gzip and Brotli compression to the configured
/// compression, Sync WALs to SyncCompressed WALs, and WALs of versions before 2.1.0 to the
/// current WAL format, because version 2.2.0 removes them.
/// It backs up the database directories, converts the files that need it in place, and the
/// loader then moves the database to the current <see cref="ZoneTreeMeta.DbVersion"/>.
/// </summary>
/// <remarks>
/// A converted file replaces the old one atomically. After a crash each file is in either
/// form, which both load, and the next migration continues. The backup is kept.
/// </remarks>
sealed class ZstdMigration<TKey, TValue>
{
  const string BackupSuffix = ".zstd-migration-backup-";

  const string ConvertedSuffix = ".zstd";

  static readonly string[] CompressedSegmentWalEndings =
      [.. from mode in new[] { WriteAheadLogMode.SyncCompressed, WriteAheadLogMode.AsyncCompressed }
          select $"{WriteAheadLogProvider.Xxh3WalExtension}.{(int)mode}"];

  static readonly string SyncWalEnding =
      $"{WriteAheadLogProvider.Xxh3WalExtension}.{(int)WriteAheadLogMode.Sync}";

  /// <summary>
  /// The WALs of versions before 2.0, which have no XXH3 record checksums.
  /// </summary>
  static readonly string[] CrcWalEndings =
      [.. from mode in new[] { WriteAheadLogMode.Sync, WriteAheadLogMode.SyncCompressed, WriteAheadLogMode.AsyncCompressed }
          select $"{WriteAheadLogProvider.LegacyWalExtension}.{(int)mode}"];

  readonly ZoneTreeOptions<TKey, TValue> Options;

  readonly IFileStreamProvider Files;

  readonly IDurableFileOperations DurableFiles;

  readonly string DataDirectory;

  // A custom WAL provider owns its files. The loader reports the legacy ones.
  readonly string WalDirectory;

  public ZstdMigration(ZoneTreeOptions<TKey, TValue> options)
  {
    Options = options;
    Files = options.RandomAccessDeviceManager.FileStreamProvider;
    DurableFiles = Files.GetDurableFileOperations();
    DataDirectory = Path.GetDirectoryName(options.RandomAccessDeviceManager.GetFilePath(0, ""));
    WalDirectory = (options.WriteAheadLogProvider as WriteAheadLogProvider)?.WalDirectory;
  }

  public void Run(ZoneTreeMeta meta)
  {
    if (meta.DbVersion != null && Version.Parse(meta.DbVersion) >= ZoneTreeInfo.DbVersion)
      return;
    LegacyFormats.ThrowIfSelected(
        Options.WriteAheadLogOptions,
        Options.DiskSegmentOptions,
        "ZoneTree 2.2.0 does not support it, so the migration cannot convert the database to it");
    LegacyFormats.ThrowIfLegacyWalCompression(
        Options.WriteAheadLogOptions,
        "The migration compresses the Sync WALs with it, and ZoneTree 2.2.0 does not support it");

    var dataFiles = Files.GetFiles(DataDirectory);
    var devices = dataFiles
        .Where(path => path.EndsWith(RandomAccessDeviceManager.CompressedFileExtension, StringComparison.Ordinal))
        .Where(path => LegacyFormats.IsLegacy(
            CompressedFileRandomAccessDevice.ReadCompressionMethod(Files, path)))
        .ToArray();
    var multiPartHeaders = dataFiles
        .Where(path => path.EndsWith(DiskSegmentConstants.MultiPartDiskSegmentCategory, StringComparison.Ordinal))
        .Where(path =>
        {
          MultiPartMetadataCodec.Decode(Files.ReadAllBytes(path), out var method);
          return LegacyFormats.IsLegacy(method);
        })
        .ToArray();
    var walFiles = GetWalFiles();
    var wals = walFiles
        .Where(IsCompressedWal)
        .Where(path =>
        {
          var method = CompressedFileStream.ReadCompressionMethod(Files, path, out var hasLegacyBlocks);
          return hasLegacyBlocks || LegacyFormats.IsLegacy(method);
        })
        .ToArray();
    var rewrittenWals = walFiles.Where(path => IsSyncWal(path) || IsCrcWal(path)).ToArray();
    if (devices.Length == 0 && multiPartHeaders.Length == 0 && wals.Length == 0 && rewrittenWals.Length == 0)
      return;

    Backup();
    foreach (var path in devices)
      ConvertDevice(path);
    foreach (var path in multiPartHeaders)
      ConvertMultiPartHeader(path);
    foreach (var path in wals)
      ConvertWal(path);
    foreach (var path in rewrittenWals)
      RewriteWal(path);
    // The WAL replacement renames and the WAL deletions do not sync the directory.
    // Without a sync, a power loss could bring back a legacy WAL after the loader moves
    // the database to the current DbVersion.
    foreach (var directory in wals.Concat(rewrittenWals).Select(Path.GetDirectoryName).Distinct())
      Files.SyncDirectory(directory);
  }

  /// <summary>
  /// Returns the WAL files of the segments and the chunks of a shared WAL. Other directories
  /// in the WAL directory, such as the directory of another tree, are not searched.
  /// </summary>
  string[] GetWalFiles()
  {
    if (WalDirectory == null)
      return [];
    var segmentWals = Path.Combine(WalDirectory, ZoneTree<TKey, TValue>.SegmentWalCategory);
    var sharedWal = Path.Combine(segmentWals, SharedWalStore<TKey, TValue>.DirectoryName);
    return [.. FilesIn(segmentWals), .. FilesIn(sharedWal)];
  }

  IReadOnlyList<string> FilesIn(string directory) =>
      Files.DirectoryExists(directory) ? Files.GetFiles(directory) : [];

  static bool IsCompressedWal(string path)
  {
    var name = Path.GetFileName(path);
    if (Path.GetFileName(Path.GetDirectoryName(path)) == SharedWalStore<TKey, TValue>.DirectoryName)
      return name.EndsWith(SharedWalStore<TKey, TValue>.ChunkExtension, StringComparison.Ordinal);
    return CompressedSegmentWalEndings.Any(ending => name.EndsWith(ending, StringComparison.Ordinal));
  }

  static bool IsSyncWal(string path) =>
      Path.GetFileName(path).EndsWith(SyncWalEnding, StringComparison.Ordinal);

  static bool IsCrcWal(string path) =>
      CrcWalEndings.Any(ending => Path.GetFileName(path).EndsWith(ending, StringComparison.Ordinal));

  void Backup()
  {
    var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
    BackupDirectory(DataDirectory, timestamp);
    if (WalDirectory != null && !IsWithin(WalDirectory, DataDirectory))
      BackupDirectory(WalDirectory, timestamp);
  }

  static bool IsWithin(string directory, string parent)
  {
    var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    var parentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
    return path == parentPath || path.StartsWith(parentPath + Path.DirectorySeparatorChar, StringComparison.Ordinal);
  }

  void BackupDirectory(string directory, string timestamp)
  {
    var backup = Path.TrimEndingDirectorySeparator(directory) + BackupSuffix + timestamp;
    Options.Logger?.LogWarning(
        $"ZoneTree backs up {directory} to {backup} and migrates the database for ZoneTree 2.2.0. " +
        "Delete the backup after you check that the database works.");
    CopyDirectory(directory, backup);
  }

  void CopyDirectory(string source, string destination)
  {
    DurableFiles.CreateDirectory(destination);
    foreach (var file in Files.GetFiles(source))
      CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));
    foreach (var directory in Files.GetDirectories(source))
      CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    Files.SyncDirectory(destination);
  }

  void CopyFile(string source, string destination)
  {
    using var from = Files.CreateFileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var to = Files.CreateFileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    from.ToStream().CopyTo(to.ToStream());
    to.Flush(true);
  }

  /// <summary>
  /// Copies the blocks of a disk segment file into a file with the same block size, so the
  /// positions that the segment holds stay valid.
  /// </summary>
  void ConvertDevice(string path)
  {
    var converted = path + ConvertedSuffix;
    if (Files.FileExists(converted))
      Files.DeleteFile(converted);
    var options = Options.DiskSegmentOptions;
    // The manager has no device under these keys, so closing them removes nothing.
    var deviceManager = Options.RandomAccessDeviceManager;
    using (var source = new CompressedFileRandomAccessDevice(
        Options.Logger, Files, 0, path, deviceManager, path, false,
        0, options.CompressionMethod, options.CompressionLevel))
    using (var target = new CompressedFileRandomAccessDevice(
        Options.Logger, Files, 0, converted, deviceManager, converted, true,
        source.BlockSize, options.CompressionMethod, options.CompressionLevel))
    {
      var length = source.Length;
      for (var offset = 0L; offset < length; offset += source.BlockSize)
        target.AppendBytesReturnPosition(
            source.GetBytes(offset, (int)Math.Min(source.BlockSize, length - offset)).Span);
      target.SealDevice();
    }
    DurableFiles.MoveFile(converted, path, overwrite: true);
  }

  void ConvertMultiPartHeader(string path)
  {
    var decoded = MultiPartMetadataCodec.Decode(Files.ReadAllBytes(path), out _);
    DurableFiles.WriteAllBytes(path, MultiPartMetadataCodec.Encode(decoded).Span);
  }

  CompressedFileStream OpenWal(string path)
  {
    var options = Options.WriteAheadLogOptions;
    return new CompressedFileStream(
        Options.Logger, Files, path, options.CompressionBlockSize, false, 0,
        options.CompressionMethod, options.CompressionLevel);
  }

  /// <summary>
  /// Reads the content of a WAL as the WAL reads it, with its records, their op indexes
  /// and a torn last record as they are.
  /// </summary>
  static byte[] ReadContent(CompressedFileStream stream, string path)
  {
    // Setting the position does not move the stream to the first block.
    stream.Seek(0, SeekOrigin.Begin);
    using var reader = new BinaryReader(stream, Encoding.UTF8, true);
    var content = reader.ReadBytes(checked((int)stream.Length));
    if (content.Length != stream.Length)
      throw new InvalidDataException(
          $"The WAL {path} could not be read to its end. Restore the database from the migration backup.");
    return content;
  }

  /// <summary>
  /// Copies the content of a WAL file or a shared WAL chunk in the current format.
  /// </summary>
  void ConvertWal(string path)
  {
    var options = Options.WriteAheadLogOptions;
    var stream = OpenWal(path);
    byte[] content;
    try
    {
      content = ReadContent(stream, path);
    }
    catch
    {
      stream.Dispose();
      throw;
    }
    // Closes the stream.
    stream.ReplaceContent(
        Files,
        writer => writer.Write(content),
        options.CompressionMethod,
        options.CompressionLevel);
  }

  /// <summary>
  /// Rewrites a Sync WAL as a SyncCompressed WAL of the same segment, and a WAL of a version
  /// before 2.0 as a WAL of the current format with the same mode. The records are copied as
  /// they are, with their op indexes, and the records of a version before 2.0 get XXH3
  /// checksums. The Sync WAL reader checks the records of a Sync WAL and leaves out a torn
  /// last record. It reads keys and values as bytes, because a transactional tree stores
  /// versioned records, not records of the tree types.
  /// </summary>
  /// <remarks>
  /// While both WALs exist, the provider loads a Sync WAL, so a crash before its deletion
  /// leaves it in use, and the next migration converts it again. It loads the current format
  /// before the format of versions before 2.0, so the next migration deletes the old WAL.
  /// </remarks>
  void RewriteWal(string path)
  {
    var isCrcWal = IsCrcWal(path);
    var mode = (WriteAheadLogMode)(path[^1] - '0');
    var targetMode = mode == WriteAheadLogMode.Sync ? WriteAheadLogMode.SyncCompressed : mode;
    var extension = isCrcWal ? WriteAheadLogProvider.LegacyWalExtension : WriteAheadLogProvider.Xxh3WalExtension;
    var target = path[..^(extension.Length + 2)] + WriteAheadLogProvider.Xxh3WalExtension + "." +
        ((int)targetMode).ToString(CultureInfo.InvariantCulture);
    if (isCrcWal && Files.FileExists(target))
    {
      DeleteWal(path, mode);
      return;
    }

    byte[] records;
    if (mode == WriteAheadLogMode.Sync)
      records = ReadSyncWalRecords(path);
    else
      using (var stream = OpenWal(path))
        records = ReadContent(stream, path);
    if (isCrcWal)
      WriteChecksums(records);

    var converted = target + ConvertedSuffix;
    var convertedTail = converted + ".tail";
    if (Files.FileExists(converted))
      Files.DeleteFile(converted);
    if (Files.FileExists(convertedTail))
      Files.DeleteFile(convertedTail);
    using (var stream = OpenWal(converted))
    {
      stream.Write(records);
      stream.SealTail();
    }
    // The sealed main file holds the records, so the target opens with a new tail.
    Files.DeleteFile(convertedTail);
    DurableFiles.MoveFile(converted, target, overwrite: true);
    DeleteWal(path, mode);
  }

  byte[] ReadSyncWalRecords(string path)
  {
    long length;
    var bytes = new ByteArraySerializer();
    using (var wal = new SyncFileSystemWriteAheadLog<Memory<byte>, Memory<byte>>(
        Options.Logger, Files, bytes, bytes, path))
    {
      var result = wal.ReadLogEntries(false, false, false);
      if (!result.Success && !result.HasFoundIncompleteTailRecord)
        throw new WriteAheadLogCorruptionException(
            long.Parse(Path.GetFileName(path).Split('.')[0], CultureInfo.InvariantCulture),
            result.Exceptions);
      length = result.HasFoundIncompleteTailRecord ? result.IncompleteTailRecord.RecordPosition : -1;
    }
    var records = Files.ReadAllBytes(path);
    return length >= 0 ? records[..(int)length] : records;
  }

  /// <summary>
  /// Writes the XXH3 checksums of the records. A torn last record stays as it is.
  /// </summary>
  static void WriteChecksums(Span<byte> records)
  {
    while (records.Length >= LogEntry.MinimumRecordLength)
    {
      var length = LogEntry.ReadRecordLength(records);
      if (length < 0 || length > records.Length)
        return;
      LogEntry.WriteChecksum(records[..(int)length]);
      records = records[(int)length..];
    }
  }

  void DeleteWal(string path, WriteAheadLogMode mode)
  {
    var tail = path + ".tail";
    if (mode != WriteAheadLogMode.Sync && Files.FileExists(tail))
      Files.DeleteFile(tail);
    Files.DeleteFile(path);
  }
}
