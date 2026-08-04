namespace ZoneTree.AbstractFileStream;

public sealed class LocalFileStreamProvider : IFileStreamProvider
{
  static readonly int[] ReplaceRetryDelaysMilliseconds = { 10, 25, 50, 100, 200 };

  readonly Action<string, string, string> ReplaceFile;

  readonly Action<int> Sleep;

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

  public void Replace(
      string sourceFileName,
      string destinationFileName,
      string destinationBackupFileName)
  {
    // File Replace is a fast operation in local filesystem. 
    // It uses file rename operation and it is atomic.
    for (var attempt = 0; ; ++attempt)
    {
      try
      {
        ReplaceFile(sourceFileName, destinationFileName, destinationBackupFileName);
        return;
      }
      catch (IOException exception) when (CanRetryReplace(exception, attempt))
      {
        Sleep(ReplaceRetryDelaysMilliseconds[attempt]);
      }
    }
  }

  static bool CanRetryReplace(IOException exception, int attempt)
  {
    return attempt < ReplaceRetryDelaysMilliseconds.Length &&
        exception is not FileNotFoundException &&
        exception is not DirectoryNotFoundException &&
        exception is not DriveNotFoundException &&
        exception is not PathTooLongException;
  }

  public DurableFileWriter GetDurableFileWriter()
  {
    return new DurableFileWriter(this);
  }

  public IReadOnlyList<string> GetDirectories(string path)
  {
    return Directory.GetDirectories(path);
  }

  public string CombinePaths(string path1, string path2)
  {
    return Path.Combine(path1, path2);
  }
}
