using System.Globalization;
using System.Text;
using ZoneTree.AbstractFileStream;
using ZoneTree.Compression;
using ZoneTree.Options;
using ZoneTree.Segments.Disk;
using ZoneTree.Segments.MultiPart;
using ZoneTree.Segments.RandomAccess;
using ZoneTree.WAL;

namespace ZoneTree.Core;

/// <summary>
/// Migrates the files of a database from LZ4, Gzip and Brotli compression, which version
/// 2.2.0 removes, to the configured compression. It backs up the database directories,
/// converts the files that need it in place, and the loader then moves the database to the
/// current <see cref="ZoneTreeMeta.DbVersion"/>.
/// </summary>
/// <remarks>
/// A converted file replaces the old one atomically. After a crash each file is in either
/// form, which both load, and the next migration continues. The backup is kept.
/// </remarks>
sealed class ZstdMigration<TKey, TValue>
{
  const string BackupSuffix = ".zstd-migration-backup-";

  const string ConvertedSuffix = ".zstd";

  // The device manager stores compressed devices with this extension.
  const string CompressedDeviceExtension = ".z";

  static readonly string[] CompressedSegmentWalEndings =
      (from extension in new[] { WriteAheadLogProvider.Xxh3WalExtension, WriteAheadLogProvider.LegacyWalExtension }
       from mode in new[] { WriteAheadLogMode.SyncCompressed, WriteAheadLogMode.AsyncCompressed }
       select $"{extension}.{(int)mode}").ToArray();

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
    LegacyCompression.ThrowIfSelected(
        Options.WriteAheadLogOptions,
        Options.DiskSegmentOptions,
        "The migration from LZ4, Gzip and Brotli compression needs Zstd or None");

    var dataFiles = Files.GetFiles(DataDirectory);
    var devices = dataFiles
        .Where(path => path.EndsWith(CompressedDeviceExtension, StringComparison.Ordinal))
        .Where(path => LegacyCompression.IsLegacy(
            CompressedFileRandomAccessDevice.ReadCompressionMethod(Files, path)))
        .ToArray();
    var multiPartHeaders = dataFiles
        .Where(path => path.EndsWith(DiskSegmentConstants.MultiPartDiskSegmentCategory, StringComparison.Ordinal))
        .Where(path =>
        {
          MultiPartMetadataCodec.Decode(Files.ReadAllBytes(path), out var method);
          return LegacyCompression.IsLegacy(method);
        })
        .ToArray();
    var wals = WalDirectory == null
        ? []
        : GetFilesRecursively(WalDirectory)
            .Where(IsCompressedWal)
            .Where(path => LegacyCompression.IsLegacy(CompressedFileStream.ReadCompressionMethod(Files, path)))
            .ToArray();
    if (devices.Length == 0 && multiPartHeaders.Length == 0 && wals.Length == 0)
      return;

    Backup();
    foreach (var path in devices)
      ConvertDevice(path);
    foreach (var path in multiPartHeaders)
      ConvertMultiPartHeader(path);
    foreach (var path in wals)
      ConvertWal(path);
    // The WAL replacement renames without a directory sync. Without one, a power loss
    // could bring back a legacy WAL after the loader moves the database to the current
    // DbVersion.
    foreach (var directory in wals.Select(Path.GetDirectoryName).Distinct())
      Files.SyncDirectory(directory);
  }

  IEnumerable<string> GetFilesRecursively(string directory) =>
      Files.GetFiles(directory).Concat(Files.GetDirectories(directory).SelectMany(GetFilesRecursively));

  static bool IsCompressedWal(string path)
  {
    var name = Path.GetFileName(path);
    if (Path.GetFileName(Path.GetDirectoryName(path)) == SharedWalStore<TKey, TValue>.DirectoryName)
      return name.EndsWith(SharedWalStore<TKey, TValue>.ChunkExtension, StringComparison.Ordinal);
    return CompressedSegmentWalEndings.Any(ending => name.EndsWith(ending, StringComparison.Ordinal));
  }

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
        $"ZoneTree backs up {directory} to {backup} and migrates the database to Zstd compression. " +
        "Delete the backup when the database works.");
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

  /// <summary>
  /// Copies the content of a WAL file or a shared WAL chunk as the WAL reads it, with its
  /// records, their op indexes and a torn last record as they are.
  /// </summary>
  void ConvertWal(string path)
  {
    var options = Options.WriteAheadLogOptions;
    var stream = new CompressedFileStream(
        Options.Logger, Files, path, options.CompressionBlockSize, false, 0,
        options.CompressionMethod, options.CompressionLevel);
    byte[] content;
    try
    {
      // Setting the position does not move the stream to the first block.
      stream.Seek(0, SeekOrigin.Begin);
      using var reader = new BinaryReader(stream, Encoding.UTF8, true);
      content = reader.ReadBytes(checked((int)stream.Length));
      if (content.Length != stream.Length)
        throw new InvalidDataException($"The WAL {path} could not be read to its end.");
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
}
