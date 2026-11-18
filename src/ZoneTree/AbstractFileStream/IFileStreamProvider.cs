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

  /// <summary>
  /// Makes completed namespace changes in this directory durable on platforms
  /// that require a directory sync in addition to syncing file contents.
  /// Providers whose metadata operations are already durable may do nothing.
  /// Checkpoint publication and shared WALs require this operation before
  /// deleting files superseded by newly published files or metadata.
  /// </summary>
  /// <exception cref="UnauthorizedAccessException">
  /// The process may not open the directory. Syncs that walk every directory up to the root skip such a directory.
  /// </exception>
  void SyncDirectory(string path);

  void DeleteFile(string path);

  void DeleteDirectory(string path, bool recursive);

  string ReadAllText(string path);

  byte[] ReadAllBytes(string path);

  /// <summary>
  /// Replaces the file at the destination with the source file. Without a backup, this is one atomic rename: after a
  /// crash, the destination has either its old file or the new one.
  /// </summary>
  /// <param name="sourceFileName">
  /// The path of the new file, which takes the place of the file at the destination.
  /// </param>
  /// <param name="destinationFileName">The path of the file to replace. A file must exist at this path.</param>
  /// <param name="destinationBackupFileName">
  /// The path to keep the replaced file under, or null to discard the replaced file. With a backup, a crash can also
  /// leave the old file at the backup path and no file at the destination, so a reader that finds no file at the
  /// destination should read the backup.
  /// </param>
  /// <remarks>
  /// All of the paths, including the backup path, must be on the same volume, because no file system can rename a file
  /// from one volume to another in one step.
  /// </remarks>
  void Replace(
      string sourceFileName,
      string destinationFileName,
      string destinationBackupFileName);

  DurableFileWriter GetDurableFileWriter();

  /// <summary>
  /// Returns file operations whose results are on disk as soon as they return: writing a whole file, renaming a file,
  /// and creating a directory.
  /// </summary>
  /// <remarks>
  /// The default implementation uses only this provider's other members. It writes a file to a temporary file and
  /// flushes it to disk, renames it with <see cref="Replace"/> or <see cref="MoveFile"/>, and then calls
  /// <see cref="SyncDirectory"/> so that the new name reaches the disk too. It needs a provider where:
  /// <list type="bullet">
  /// <item>every member is safe to call from several threads at once;</item>
  /// <item><see cref="Replace"/> and <see cref="MoveFile"/> each rename a file in one atomic step;</item>
  /// <item><see cref="MoveFile"/> checks for an existing file at the destination in that same step;</item>
  /// <item>a rename reaches the disk at once, or when <see cref="SyncDirectory"/> returns.</item>
  /// </list>
  /// A provider that can do this more efficiently, or more safely, returns its own implementation.
  /// <para>
  /// The default implementation creates a new object on every call, so a caller that uses the operations often should
  /// keep the instance it gets.
  /// </para>
  /// </remarks>
  IDurableFileOperations GetDurableFileOperations() => new DurableFileOperations(this);

  IReadOnlyList<string> GetDirectories(string path);

  /// <summary>
  /// Returns the paths of the files directly in the directory, without the files in its subdirectories.
  /// </summary>
  IReadOnlyList<string> GetFiles(string path);

  /// <summary>
  /// Renames a file in one atomic step, without replacing an existing file: after a crash, the file has either its old
  /// name or its new one.
  /// </summary>
  /// <param name="sourcePath">The current path of the file.</param>
  /// <param name="destinationPath">The new path of the file.</param>
  /// <exception cref="IOException">A file already exists at the destination.</exception>
  /// <remarks>
  /// Both paths must be on the same volume, because no file system can rename a file from one volume to another in one
  /// step.
  /// </remarks>
  void MoveFile(string sourcePath, string destinationPath);

  string CombinePaths(string path1, string path2);
}
