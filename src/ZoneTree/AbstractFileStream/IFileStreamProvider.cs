namespace ZoneTree.AbstractFileStream;

/// <summary>
/// Abstraction over FileStream to make it easy to integrate with
/// cloud blob / stream providers.
/// </summary>
public interface IFileStreamProvider
{
  IFileStream CreateFileStream(
      string path,
      FileMode mode,
      FileAccess access,
      FileShare share,
      int bufferSize = 4096,
      FileOptions options = FileOptions.None);

  bool FileExists(string path);

  bool DirectoryExists(string path);

  void CreateDirectory(string path);

  void DeleteFile(string path);

  void DeleteDirectory(string path, bool recursive);

  string ReadAllText(string path);

  byte[] ReadAllBytes(string path);

  void Replace(
      string sourceFileName,
      string destinationFileName,
      string destinationBackupFileName);

  DurableFileWriter GetDurableFileWriter();

  IReadOnlyList<string> GetDirectories(string path);

  /// <summary>
  /// Returns file paths directly within the directory, used to locate legacy WALs for renaming.
  /// </summary>
  IReadOnlyList<string> GetFiles(string path) => throw new NotSupportedException();

  /// <summary>
  /// Renames a file without overwriting an existing destination, used for legacy WAL format naming.
  /// </summary>
  void MoveFile(string sourcePath, string destinationPath) => throw new NotSupportedException();

  string CombinePaths(string path1, string path2);
}
