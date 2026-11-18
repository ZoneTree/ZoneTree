namespace ZoneTree.AbstractFileStream;

public sealed class LocalFileStreamProvider : IFileStreamProvider
{
  readonly Action<string, string, string> ReplaceFile;

  readonly Action<int> Sleep;

  readonly LocalDurableFileOperations Durable;

  public LocalFileStreamProvider()
      : this(File.Replace, Thread.Sleep)
  {
  }

  internal LocalFileStreamProvider(
      Action<string, string, string> replaceFile,
      Action<int> sleep)
  {
    ReplaceFile = replaceFile;
    Sleep = sleep;
    Durable = new LocalDurableFileOperations(this);
  }

  public IFileStream CreateFileStream(
      string path,
      FileMode mode,
      FileAccess access,
      FileShare share,
      int bufferSize = 4096,
      FileOptions options = FileOptions.None)
  {
    return new LocalFileStream(path, mode, access, share, bufferSize, options);
  }

  public void CreateDirectory(string path)
  {
    Directory.CreateDirectory(path);
  }

  public void SyncDirectory(string path) => DirectorySync.Flush(path);

  public bool DirectoryExists(string path)
  {
    return Directory.Exists(path);
  }

  public void DeleteDirectory(string path, bool recursive)
  {
    Directory.Delete(path, recursive);
  }

  public bool FileExists(string path)
  {
    return File.Exists(path);
  }

  public void DeleteFile(string path)
  {
    File.Delete(path);
  }

  public string ReadAllText(string path)
  {
    return File.ReadAllText(path);
  }

  public byte[] ReadAllBytes(string path)
  {
    return File.ReadAllBytes(path);
  }

  /// <inheritdoc/>
  /// <remarks>
  /// <para>
  /// Without a backup, this is a plain rename, so it also works when no file exists at the destination yet, and the new
  /// file keeps its own permissions instead of taking over those of the file it replaces. On Windows, the rename is
  /// written to disk before this method returns, because the directory sync does nothing on Windows. On Linux and
  /// macOS, it reaches the disk when the caller calls <see cref="SyncDirectory"/>.
  /// </para>
  /// <para>
  /// With a backup, this uses <c>File.Replace</c>, whose renames Windows writes to disk on its own schedule.
  /// </para>
  /// </remarks>
  public void Replace(
      string sourceFileName,
      string destinationFileName,
      string destinationBackupFileName)
  {
    // File.Replace is avoided without a backup: it fails when the destination does not exist, and on Windows its
    // ReplaceFile can leave no file at the destination at all if it fails halfway.
    if (destinationBackupFileName == null)
    {
      LocalFileRename.Rename(sourceFileName, destinationFileName, replace: true, writeThrough: true);
      return;
    }
    // On Windows, File.Replace fails while another process has any of the three files open.
    HeldFileRetry.Run(() => ReplaceFile(sourceFileName, destinationFileName, destinationBackupFileName), Sleep);
  }

  public DurableFileWriter GetDurableFileWriter()
  {
    return new DurableFileWriter(this);
  }

  public IDurableFileOperations GetDurableFileOperations() => Durable;

  public IReadOnlyList<string> GetDirectories(string path)
  {
    return Directory.GetDirectories(path);
  }

  public string CombinePaths(string path1, string path2)
  {
    return Path.Combine(path1, path2);
  }

  public IReadOnlyList<string> GetFiles(string path) => Directory.GetFiles(path);

  /// <inheritdoc/>
  /// <remarks>
  /// <para>
  /// On Windows, where the directory sync does nothing, the rename is written to disk before this method returns. On
  /// Linux and macOS, it reaches the disk when the caller calls <see cref="SyncDirectory"/>.
  /// </para>
  /// <para>
  /// On Linux and macOS, this uses the system's rename that refuses to replace a file. A few systems lack that rename:
  /// FreeBSD, Linux with a C library older than glibc 2.28 or a kernel older than 3.15, and file systems that do not
  /// support it, such as NFS. There, this falls back to <c>File.Move</c>, which checks that the destination is free and
  /// then renames the file. A crash still leaves the file under exactly one name, but a file that another process
  /// creates at the destination in between is replaced.
  /// </para>
  /// </remarks>
  public void MoveFile(string sourcePath, string destinationPath)
      => LocalFileRename.Rename(sourcePath, destinationPath, replace: false, writeThrough: true,
          NoReplaceFallback.CheckFirst);
}
