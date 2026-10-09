using System.Text;

namespace ZoneTree.AbstractFileStream;

/// <summary>
/// Provides fast, in-process, RAM-backed file streams for ZoneTree workflows.
/// This provider is optimized for WAL, segment, metadata, and merge operations
/// that use stream-based access. It is not durable, does not emulate every
/// filesystem behavior, and intentionally ignores <see cref="FileShare"/>.
/// Large files are stored in chunks, while <see cref="ReadAllBytes"/> remains a
/// small-file convenience API and may throw when a file cannot fit in one array.
/// </summary>
public sealed class InMemoryFileStreamProvider : IFileStreamProvider
{
  readonly Dictionary<string, InMemoryFile> Files = [];

  readonly HashSet<string> Directories = [];

  readonly Lock SyncRoot = new();

  public IFileStream CreateFileStream(
      string path,
      FileMode mode,
      FileAccess access,
      FileShare share,
      int bufferSize = 4096,
      FileOptions options = FileOptions.None)
  {
    path = NormalizePath(path);
    lock (SyncRoot)
    {
      var fileExists = Files.TryGetValue(path, out var file);
      switch (mode)
      {
        case FileMode.CreateNew:
          ThrowIfNoWriteAccess(access, mode);
          if (fileExists)
            throw new IOException($"File {path} already exists.");
          file = CreateFile(path);
          break;
        case FileMode.Create:
          ThrowIfNoWriteAccess(access, mode);
          file = fileExists ? ClearFile(file) : CreateFile(path);
          break;
        case FileMode.Open:
          if (!fileExists)
            throw new FileNotFoundException(path);
          break;
        case FileMode.OpenOrCreate:
          if (!fileExists)
            ThrowIfNoWriteAccess(access, mode);
          if (!fileExists)
            file = CreateFile(path);
          break;
        case FileMode.Truncate:
          if (!fileExists)
            throw new FileNotFoundException(path);
          ThrowIfNoWriteAccess(access, mode);
          file.SetLength(0);
          break;
        case FileMode.Append:
          if (access != FileAccess.Write)
            throw new ArgumentException("FileMode.Append requires FileAccess.Write.", nameof(access));
          if (!fileExists)
            file = CreateFile(path);
          break;
        default:
          throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
      }

      var stream = new InMemoryFileStream(path, file, access, mode == FileMode.Append);
      if (mode == FileMode.Append)
        stream.Seek(0, SeekOrigin.End);
      return stream;
    }
  }

  static void ThrowIfNoWriteAccess(FileAccess access, FileMode mode)
  {
    if ((access & FileAccess.Write) == 0)
      throw new ArgumentException($"{mode} requires write access.", nameof(access));
  }

  InMemoryFile CreateFile(string path)
  {
    EnsureParentDirectories(path);
    var file = new InMemoryFile();
    Files[path] = file;
    return file;
  }

  static InMemoryFile ClearFile(InMemoryFile file)
  {
    file.SetLength(0);
    return file;
  }

  void EnsureParentDirectories(string path)
  {
    var parent = GetParentDirectory(path);
    if (parent.Length == 0)
      return;

    var parts = parent.Split('/');
    var current = "";
    foreach (var part in parts)
    {
      current = current.Length == 0 ? part : current + "/" + part;
      Directories.Add(current);
    }
  }

  static string GetParentDirectory(string path)
  {
    var index = path.LastIndexOf('/');
    return index <= 0 ? "" : path[..index];
  }

  static bool IsDescendant(string path, string parent)
  {
    return path.Length > parent.Length &&
           path.StartsWith(parent, StringComparison.Ordinal) &&
           path[parent.Length] == '/';
  }

  static string NormalizePath(string path)
  {
    ArgumentNullException.ThrowIfNull(path);
    path = path.Replace('\\', '/');
    while (path.Contains("//", StringComparison.Ordinal))
      path = path.Replace("//", "/", StringComparison.Ordinal);
    return path.Length > 1 ? path.TrimEnd('/') : path;
  }

  public bool FileExists(string path)
  {
    path = NormalizePath(path);
    lock (SyncRoot) return Files.ContainsKey(path);
  }

  public bool DirectoryExists(string path)
  {
    path = NormalizePath(path);
    lock (SyncRoot) return Directories.Contains(path);
  }

  public void CreateDirectory(string path)
  {
    path = NormalizePath(path);
    lock (SyncRoot)
    {
      if (path.Length == 0)
        return;
      EnsureParentDirectories(path + "/file");
      Directories.Add(path);
    }
  }

  public void DeleteFile(string path)
  {
    path = NormalizePath(path);
    lock (SyncRoot) Files.Remove(path);
  }

  public void DeleteDirectory(string path, bool recursive)
  {
    path = NormalizePath(path);
    lock (SyncRoot)
    {
      if (!recursive &&
          (Files.Keys.Any(x => GetParentDirectory(x) == path) ||
           Directories.Any(x => IsDescendant(x, path))))
      {
        throw new IOException($"Directory {path} is not empty.");
      }

      Directories.Remove(path);
      if (recursive)
      {
        foreach (var file in Files.Keys.Where(x => IsDescendant(x, path)).ToArray())
          Files.Remove(file);
        foreach (var directory in Directories.Where(x => IsDescendant(x, path)).ToArray())
          Directories.Remove(directory);
      }
    }
  }

  public string ReadAllText(string path)
  {
    return Encoding.UTF8.GetString(ReadAllBytes(path));
  }

  public byte[] ReadAllBytes(string path)
  {
    path = NormalizePath(path);
    lock (SyncRoot)
    {
      if (!Files.TryGetValue(path, out var file))
        throw new FileNotFoundException(path);
      return file.ToArray();
    }
  }

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

    lock (SyncRoot)
    {
      if (!Files.TryGetValue(sourceFileName, out var source))
        throw new FileNotFoundException(sourceFileName);

      if (destinationBackupFileName != null &&
          Files.TryGetValue(destinationFileName, out var destination))
      {
        EnsureParentDirectories(destinationBackupFileName);
        Files[destinationBackupFileName] = destination;
      }

      EnsureParentDirectories(destinationFileName);
      Files[destinationFileName] = source;
      Files.Remove(sourceFileName);
    }
  }

  public DurableFileWriter GetDurableFileWriter()
  {
    return new DurableFileWriter(this);
  }

  public IReadOnlyList<string> GetDirectories(string path)
  {
    path = NormalizePath(path);
    lock (SyncRoot)
    {
      return Directories
          .Where(x => GetParentDirectory(x) == path)
          .ToArray();
    }
  }

  public string CombinePaths(string path1, string path2)
  {
    return NormalizePath(Path.Combine(path1, path2));
  }

  public IReadOnlyList<string> GetFiles(string path)
  {
    path = NormalizePath(path);
    lock (SyncRoot)
      return Files.Keys.Where(x => GetParentDirectory(x) == path).ToArray();
  }

  public void MoveFile(string sourcePath, string destinationPath)
  {
    sourcePath = NormalizePath(sourcePath);
    destinationPath = NormalizePath(destinationPath);
    lock (SyncRoot)
    {
      if (!Files.TryGetValue(sourcePath, out var file))
        throw new FileNotFoundException(sourcePath);
      if (Files.ContainsKey(destinationPath))
        throw new IOException($"File {destinationPath} already exists.");
      EnsureParentDirectories(destinationPath);
      Files.Add(destinationPath, file);
      Files.Remove(sourcePath);
    }
  }
}
