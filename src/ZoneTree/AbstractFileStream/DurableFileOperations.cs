using System.Globalization;

namespace ZoneTree.AbstractFileStream;

/// <summary>
/// The default durable file operations, built only from members that every file stream provider has.
/// </summary>
/// <remarks>
/// A whole file is first written to a temporary file of its own next to the target and flushed to disk, so its content
/// is complete before it gets its final name. The temporary file is then renamed with
/// <see cref="IFileStreamProvider.Replace"/> or <see cref="IFileStreamProvider.MoveFile"/>, and the directory is
/// synced with <see cref="IFileStreamProvider.SyncDirectory"/>, which writes the new name to disk.
/// </remarks>
internal class DurableFileOperations : IDurableFileOperations
{
  const string TemporaryExtension = ".tmp";

  /// <summary>The provider whose files these operations write.</summary>
  protected IFileStreamProvider Files { get; }

  internal DurableFileOperations(IFileStreamProvider files)
  {
    Files = files;
  }

  public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes) => Write(path, bytes, overwrite: true);

  public void WriteNewFile(string path, ReadOnlySpan<byte> bytes)
  {
    if (Files.FileExists(path))
      throw new IOException($"The file '{path}' already exists.");
    Write(path, bytes, overwrite: false);
  }

  public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
      => Move(sourcePath, destinationPath, overwrite, sourceIsTemporary: false);

  /// <summary>Renames a file, and then syncs the directories that hold its old and its new name.</summary>
  void Move(string sourcePath, string destinationPath, bool overwrite, bool sourceIsTemporary)
  {
    Rename(sourcePath, destinationPath, overwrite, sourceIsTemporary);
    // The source's directory needs its own sync only when it differs: without it, a power loss could bring the old
    // name back next to the new one.
    var destinationDirectory = DirectoryOf(destinationPath);
    var sourceDirectory = DirectoryOf(sourcePath);
    SyncDirectory(destinationDirectory);
    if (!string.Equals(sourceDirectory, destinationDirectory, StringComparison.Ordinal))
      SyncDirectory(sourceDirectory);
  }

  public virtual void CreateDirectory(string path)
  {
    // With a trailing separator, Path.GetDirectoryName would return the directory itself as its parent. Without ".."
    // resolved first, "missing/../data" would lead to a sync of "missing", which is never created.
    path = DirectorySync.WithoutDotSegments(Path.TrimEndingDirectorySeparator(path));
    Files.CreateDirectory(path);
    // Directories that already exist are synced too: an earlier call may have created them and crashed before their
    // names reached the disk, and nothing tells such a directory apart from one whose name is on disk.
    DirectorySync.SyncUpToTheRoot(path, SyncDirectory);
  }

  /// <summary>
  /// Renames a file in one atomic step. The rename does not need to be on disk when this method returns, because the
  /// directories it changed are synced afterwards.
  /// </summary>
  /// <param name="sourcePath">The current path of the file.</param>
  /// <param name="destinationPath">The new path of the file.</param>
  /// <param name="overwrite">True to replace a file at the destination; false to fail if a file exists there.</param>
  /// <param name="sourceIsTemporary">
  /// True when the source is the temporary file of a write, whose random name is never written to again. An override
  /// that cannot rename such a file in one atomic step without replacing a file may rename it in steps, as long as no
  /// step replaces a file: a crash between the steps then leaves only an extra name, which is safe to delete. The
  /// default implementation ignores it, because the provider's <see cref="IFileStreamProvider.MoveFile"/> is atomic.
  /// </param>
  protected virtual void Rename(string sourcePath, string destinationPath, bool overwrite, bool sourceIsTemporary)
  {
    // The provider's Replace needs an existing file at the destination. When no file exists there, the file is renamed
    // with MoveFile instead, which never replaces a file.
    if (overwrite && Files.FileExists(destinationPath))
    {
      try
      {
        Files.Replace(sourcePath, destinationPath, null);
        return;
      }
      catch (FileNotFoundException) when (Files.FileExists(sourcePath) && !Files.FileExists(destinationPath))
      {
        // Another call deleted the destination after the check above, so there is nothing left to replace. The file
        // is renamed below instead.
      }
    }
    try
    {
      Files.MoveFile(sourcePath, destinationPath);
    }
    catch (IOException) when (overwrite && Files.FileExists(destinationPath))
    {
      // Another write created the destination after the check above, so MoveFile refused to replace it. The file is
      // replaced now instead.
      Files.Replace(sourcePath, destinationPath, null);
    }
  }

  /// <summary>Writes the names stored in a directory to disk.</summary>
  protected virtual void SyncDirectory(string path) => Files.SyncDirectory(path);

  /// <summary>Returns the directory that contains a path, or the current directory for a bare file name.</summary>
  static string DirectoryOf(string path) => Path.GetDirectoryName(path) is { Length: > 0 } directory ? directory : ".";

  void Write(string path, ReadOnlySpan<byte> bytes, bool overwrite)
  {
    // The random name keeps concurrent writes to one path apart. CreateNew guarantees that a write never fills a file
    // that an earlier write left behind, even if a random name comes up twice.
    var temporary = TemporaryPathOf(path);
    var file = Files.CreateFileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    try
    {
      using (file)
      {
        file.Write(bytes);
        file.Flush(flushToDisk: true);
      }
      Move(temporary, path, overwrite, sourceIsTemporary: true);
    }
    catch
    {
      DeleteTemporary(temporary);
      throw;
    }
  }

  /// <summary>
  /// Returns a new temporary path for a write to the path: the path with a random part and <c>.tmp</c> added.
  /// </summary>
  internal static string TemporaryPathOf(string path)
      => path + "." + Random.Shared.NextInt64().ToString("x16", CultureInfo.InvariantCulture) + TemporaryExtension;

  /// <summary>
  /// Deletes the temporary file of a write that failed. If the file cannot be deleted, it is left behind, and the
  /// error from the delete is ignored, so that the caller always sees the error that made the write fail. A temporary
  /// file left behind is always safe to delete later.
  /// </summary>
  void DeleteTemporary(string temporary)
  {
    try
    {
      if (Files.FileExists(temporary))
        Files.DeleteFile(temporary);
    }
    catch (Exception)
    {
      // A provider can fail in ways beyond the usual file errors, and none of them may hide the original error.
    }
  }
}
