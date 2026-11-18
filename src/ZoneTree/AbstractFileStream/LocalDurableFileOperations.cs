namespace ZoneTree.AbstractFileStream;

/// <summary>The durable file operations for files on the local disk.</summary>
/// <remarks>
/// On Linux and macOS, each rename is followed by a sync of the directories it changed, so the new name reaches the
/// disk.
/// <para>
/// Windows has no documented way to sync a directory, so there the directory sync does nothing. Instead, each rename
/// goes through a file handle opened with write-through, which writes the rename to disk immediately.
/// </para>
/// </remarks>
internal sealed class LocalDurableFileOperations : DurableFileOperations
{
  readonly Action<string> SyncDirectoryAction;

  internal LocalDurableFileOperations(LocalFileStreamProvider files)
      : this(files, DirectorySync.Flush)
  {
  }

  /// <summary>
  /// Creates the operations with a replacement directory sync, so tests can see which directories are synced.
  /// </summary>
  internal LocalDurableFileOperations(LocalFileStreamProvider files, Action<string> syncDirectory)
      : base(files)
  {
    SyncDirectoryAction = syncDirectory;
  }

  /// <summary>
  /// Creates a directory, together with any parent directories that do not exist yet, and syncs the directory and every
  /// directory above it.
  /// </summary>
  /// <remarks>
  /// The path is first made full, the same way <c>Directory.CreateDirectory</c> makes it. A relative path therefore
  /// has the directories above the current directory synced too, which the default operations cannot do for a
  /// provider whose current directory they do not know.
  /// </remarks>
  public override void CreateDirectory(string path) => base.CreateDirectory(Path.GetFullPath(path));

  /// <remarks>
  /// A public move that must not replace a file fails where the system cannot make it in one atomic step, because it
  /// promises both that no file is replaced and that a crash leaves one name. The temporary file of a write is moved by
  /// a hard link there instead: a second name left by a crash does no harm to its random name.
  /// </remarks>
  protected override void Rename(string sourcePath, string destinationPath, bool overwrite, bool sourceIsTemporary)
      => LocalFileRename.Rename(sourcePath, destinationPath, overwrite, writeThrough: true,
          sourceIsTemporary ? NoReplaceFallback.HardLink : NoReplaceFallback.Fail);

  protected override void SyncDirectory(string path) => SyncDirectoryAction(path);
}
