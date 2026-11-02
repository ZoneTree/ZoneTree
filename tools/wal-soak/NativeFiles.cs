using ZoneTree.AbstractFileStream;

namespace ZoneTree.WalSoak;

// Leave tail streams as LocalFileStream: wrapping them would disable the real mmap path.
sealed class NativeFiles : IFileStreamProvider
{
  readonly LocalFileStreamProvider files = new();
  public Action<string>? BeforeSync, AfterSync, AfterRename;
  public static bool IsJournal(string path) => path.Replace('\\', '/').Contains("/shared/", StringComparison.Ordinal) && path.EndsWith(".wal", StringComparison.Ordinal);
  public IFileStream CreateFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize = 4096, FileOptions options = FileOptions.None)
  {
    var stream = files.CreateFileStream(path, mode, access, share, bufferSize, options);
    return IsJournal(path) ? new JournalStream(stream, this) : stream;
  }
  public bool FileExists(string path) => files.FileExists(path);
  public bool DirectoryExists(string path) => files.DirectoryExists(path);
  public void CreateDirectory(string path) => files.CreateDirectory(path);
  public void SyncDirectory(string path) => files.SyncDirectory(path);
  public void DeleteFile(string path) => files.DeleteFile(path);
  public void DeleteDirectory(string path, bool recursive) => files.DeleteDirectory(path, recursive);
  public string ReadAllText(string path) => files.ReadAllText(path);
  public byte[] ReadAllBytes(string path) => files.ReadAllBytes(path);
  public void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName)
  { files.Replace(sourceFileName, destinationFileName, destinationBackupFileName); AfterRename?.Invoke(destinationFileName); }
  public DurableFileWriter GetDurableFileWriter() => new(this);
  public IReadOnlyList<string> GetDirectories(string path) => files.GetDirectories(path);
  public IReadOnlyList<string> GetFiles(string path) => files.GetFiles(path);
  public void MoveFile(string sourcePath, string destinationPath) { files.MoveFile(sourcePath, destinationPath); AfterRename?.Invoke(destinationPath); }
  public string CombinePaths(string path1, string path2) => files.CombinePaths(path1, path2);

  sealed class JournalStream(IFileStream inner, NativeFiles owner) : Stream, IFileStream
  {
    public string FilePath => inner.FilePath;
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => inner.CanSeek;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public int ReadFaster(byte[] buffer, int offset, int count) => inner.ReadFaster(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Flush() => inner.Flush();
    public void Flush(bool flushToDisk)
    {
      if (flushToDisk) owner.BeforeSync?.Invoke(FilePath);
      inner.Flush(flushToDisk);
      if (flushToDisk) owner.AfterSync?.Invoke(FilePath);
    }
    public Stream ToStream() => this;
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
  }
}
