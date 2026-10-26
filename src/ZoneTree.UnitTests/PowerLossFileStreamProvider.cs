using System.Collections.Concurrent;
using ZoneTree.AbstractFileStream;

namespace ZoneTree.UnitTests;

/// <summary>
/// An in-memory file stream provider that also keeps the content
/// a power loss would leave on the disk.
/// </summary>
/// <remarks>
/// A file keeps the content of its last disk sync (Flush(true)).
/// File creation, deletion and replacement are durable at once,
/// like metadata operations of a journaling file system.
/// </remarks>
public sealed class PowerLossFileStreamProvider : IFileStreamProvider
{
  readonly InMemoryFileStreamProvider Live = new();

  readonly Dictionary<string, byte[]> Durable = new(StringComparer.Ordinal);

  readonly Lock SyncRoot = new();

  int diskSyncCount;

  readonly ConcurrentQueue<string> replacedFiles = new();

  /// <summary>
  /// The number of disk syncs.
  /// </summary>
  public int DiskSyncCount => Volatile.Read(ref diskSyncCount);

  /// <summary>
  /// The destination files of the replacements.
  /// </summary>
  public IReadOnlyCollection<string> ReplacedFiles => replacedFiles;

  /// <summary>
  /// Makes a disk sync of the matching files fail.
  /// </summary>
  public Func<string, bool> FailDiskSync { get; set; }

  /// <summary>
  /// Blocks a disk sync of the matching files until it returns.
  /// </summary>
  public Action<string> BeforeDiskSync { get; set; }

  /// <summary>
  /// Called after every change of the durable content.
  /// </summary>
  public Action OnDurableChange { get; set; }

  public PowerLossFileStreamProvider()
  {
  }

  PowerLossFileStreamProvider(Dictionary<string, byte[]> files)
  {
    foreach (var (path, bytes) in files)
    {
      var directory = Path.GetDirectoryName(path);
      if (!string.IsNullOrEmpty(directory))
        Live.CreateDirectory(directory);
      using var stream = Live.CreateFileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
      stream.Write(bytes, 0, bytes.Length);
      Durable[path] = bytes;
    }
  }

  /// <summary>
  /// Creates a provider with the files that a power loss at this moment leaves.
  /// </summary>
  public PowerLossFileStreamProvider SimulatePowerLoss()
  {
    lock (SyncRoot)
    {
      return new PowerLossFileStreamProvider(
          Durable.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal));
    }
  }

  static string NormalizePath(string path)
  {
    path = path.Replace('\\', '/');
    while (path.Contains("//", StringComparison.Ordinal))
      path = path.Replace("//", "/", StringComparison.Ordinal);
    return path.Length > 1 ? path.TrimEnd('/') : path;
  }

  void ChangeDurable(Action change)
  {
    lock (SyncRoot)
    {
      change();
    }
    OnDurableChange?.Invoke();
  }

  void SyncToDisk(string path)
  {
    BeforeDiskSync?.Invoke(path);
    if (FailDiskSync?.Invoke(path) == true)
      throw new IOException($"Disk sync of {path} failed.");
    Interlocked.Increment(ref diskSyncCount);
    ChangeDurable(() => Durable[path] = Live.ReadAllBytes(path));
  }

  public IFileStream CreateFileStream(
      string path,
      FileMode mode,
      FileAccess access,
      FileShare share,
      int bufferSize = 4096,
      FileOptions options = FileOptions.None)
  {
    path = NormalizePath(path);
    var stream = Live.CreateFileStream(path, mode, access, share, bufferSize, options);
    lock (SyncRoot)
    {
      Durable.TryAdd(path, []);
    }
    return new PowerLossFileStream(this, path, stream);
  }

  public bool FileExists(string path) => Live.FileExists(path);

  public bool DirectoryExists(string path) => Live.DirectoryExists(path);

  public void CreateDirectory(string path) => Live.CreateDirectory(path);

  public void DeleteFile(string path)
  {
    path = NormalizePath(path);
    Live.DeleteFile(path);
    ChangeDurable(() => Durable.Remove(path));
  }

  public void DeleteDirectory(string path, bool recursive)
  {
    path = NormalizePath(path);
    Live.DeleteDirectory(path, recursive);
    ChangeDurable(() =>
    {
      foreach (var file in Durable.Keys.Where(x => x.StartsWith(path + "/", StringComparison.Ordinal)).ToArray())
        Durable.Remove(file);
    });
  }

  public string ReadAllText(string path) => Live.ReadAllText(path);

  public byte[] ReadAllBytes(string path) => Live.ReadAllBytes(path);

  public void Replace(
      string sourceFileName,
      string destinationFileName,
      string destinationBackupFileName)
  {
    sourceFileName = NormalizePath(sourceFileName);
    destinationFileName = NormalizePath(destinationFileName);
    destinationBackupFileName = destinationBackupFileName == null
        ? null
        : NormalizePath(destinationBackupFileName);
    Live.Replace(sourceFileName, destinationFileName, destinationBackupFileName);
    replacedFiles.Enqueue(destinationFileName);
    ChangeDurable(() =>
    {
      if (destinationBackupFileName != null &&
          Durable.TryGetValue(destinationFileName, out var destination))
        Durable[destinationBackupFileName] = destination;
      Durable[destinationFileName] = Durable[sourceFileName];
      Durable.Remove(sourceFileName);
    });
  }

  public DurableFileWriter GetDurableFileWriter() => new(this);

  public IReadOnlyList<string> GetDirectories(string path) => Live.GetDirectories(path);

  public IReadOnlyList<string> GetFiles(string path) => Live.GetFiles(path);

  public void MoveFile(string sourcePath, string destinationPath)
  {
    sourcePath = NormalizePath(sourcePath);
    destinationPath = NormalizePath(destinationPath);
    Live.MoveFile(sourcePath, destinationPath);
    ChangeDurable(() =>
    {
      Durable[destinationPath] = Durable[sourcePath];
      Durable.Remove(sourcePath);
    });
  }

  public string CombinePaths(string path1, string path2) => Live.CombinePaths(path1, path2);

  sealed class PowerLossFileStream : Stream, IFileStream
  {
    readonly PowerLossFileStreamProvider Provider;

    readonly IFileStream Inner;

    readonly Stream InnerStream;

    public string FilePath { get; }

    public PowerLossFileStream(PowerLossFileStreamProvider provider, string path, IFileStream inner)
    {
      Provider = provider;
      FilePath = path;
      Inner = inner;
      InnerStream = inner.ToStream();
    }

    public override bool CanRead => InnerStream.CanRead;

    public override bool CanSeek => InnerStream.CanSeek;

    public override bool CanWrite => InnerStream.CanWrite;

    public override long Length => InnerStream.Length;

    public override long Position
    {
      get => InnerStream.Position;
      set => InnerStream.Position = value;
    }

    public override void Flush() => InnerStream.Flush();

    public void Flush(bool flushToDisk)
    {
      InnerStream.Flush();
      if (flushToDisk)
        Provider.SyncToDisk(FilePath);
    }

    public override int Read(byte[] buffer, int offset, int count) => InnerStream.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => InnerStream.Read(buffer);

    public int ReadFaster(byte[] buffer, int offset, int count) => Inner.ReadFaster(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => InnerStream.Seek(offset, origin);

    public override void SetLength(long value) => InnerStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => InnerStream.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => InnerStream.Write(buffer);

    public Stream ToStream() => this;

    protected override void Dispose(bool disposing)
    {
      if (disposing)
        Inner.Dispose();
      base.Dispose(disposing);
    }
  }
}
