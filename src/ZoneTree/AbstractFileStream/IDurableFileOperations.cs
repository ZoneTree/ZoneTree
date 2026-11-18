namespace ZoneTree.AbstractFileStream;

/// <summary>
/// File operations whose results survive a crash or a power loss as soon as they return. Use them to write a whole
/// file, rename a file, or create a directory when that change must not be lost. Get an instance from
/// <see cref="IFileStreamProvider.GetDurableFileOperations"/>.
/// </summary>
/// <remarks>
/// <para>
/// A crash never leaves a partly written file. Writes to the same path may run at the same time, from one process or
/// from several. The file then holds the complete content of one of them, and a write that starts after another write
/// has returned always replaces that write's content.
/// </para>
/// <para>
/// A crash during a write can leave a temporary file next to the target. Its name is the target's name with a random
/// part and <c>.tmp</c> added, such as <c>state.3f9c2a7b1e04d856.tmp</c>. Deleting such a file never changes the
/// target, so it is safe to delete whenever no write to the same path is running.
/// </para>
/// </remarks>
public interface IDurableFileOperations
{
  /// <summary>
  /// Writes the bytes as the whole content of a file, creating the file or replacing an existing one. When this method
  /// returns, the new file is on disk. A crash never leaves a partly written file: after a crash, the path has either
  /// the previous file (or no file, if there was none) or the complete new file.
  /// </summary>
  void WriteAllBytes(string path, ReadOnlySpan<byte> bytes);

  /// <summary>
  /// Writes the bytes as the whole content of a new file. When this method returns, the new file is on disk. A crash
  /// never leaves a partly written file: after a crash, the path has either no file or the complete new file.
  /// </summary>
  /// <remarks>
  /// On the local file system on Linux and macOS, the temporary file gets its final name from the system's rename that
  /// refuses to replace a file. Where that rename is missing, the file is linked under its final name and then loses
  /// its temporary name, so a crash between the two can leave the temporary file behind. Like any temporary file of a
  /// write, it is safe to delete.
  /// </remarks>
  /// <exception cref="IOException">
  /// A file already exists at the path, for example because another call created it at the same time.
  /// </exception>
  /// <exception cref="NotSupportedException">
  /// The file system has neither a rename that refuses to replace a file nor hard links, so it cannot create a file
  /// without the risk of replacing one that another call creates at the same time.
  /// </exception>
  void WriteNewFile(string path, ReadOnlySpan<byte> bytes);

  /// <summary>
  /// Renames a file in one atomic step, so a crash leaves the file under either its old name or its new name. When this
  /// method returns, the new name is on disk. Flush the file's content to disk before calling this method.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Both paths must be on the same volume, because no file system can rename a file between volumes in one step. On
  /// the local file system, such a move fails.
  /// </para>
  /// <para>
  /// On the local file system on Linux and macOS, a move that must not replace a file uses the system's rename that
  /// refuses to replace one. A few systems lack that rename: FreeBSD, Linux with a C library older than glibc 2.28 or
  /// a kernel older than 3.15, and file systems that do not support it, such as NFS. There, such a move throws
  /// <see cref="NotSupportedException"/>, because any other way could either replace a file that another process
  /// creates at the same moment or leave the file under both names after a crash.
  /// </para>
  /// </remarks>
  /// <param name="sourcePath">The current path of the file.</param>
  /// <param name="destinationPath">The new path of the file.</param>
  /// <param name="overwrite">
  /// True to replace a file that already exists at the destination; false to fail if a file already exists there.
  /// </param>
  /// <exception cref="IOException">
  /// <paramref name="overwrite"/> is false, and a file already exists at the destination.
  /// </exception>
  /// <exception cref="NotSupportedException">
  /// <paramref name="overwrite"/> is false, and the system or the file system cannot rename a file in one atomic step
  /// without replacing an existing file.
  /// </exception>
  void MoveFile(string sourcePath, string destinationPath, bool overwrite);

  /// <summary>
  /// Creates a directory, together with any parent directories that do not exist yet. When this method returns, the
  /// directory and the names of all the directories above it are on disk. This includes directories that already
  /// existed, because a process that created them earlier may have crashed before their names reached the disk.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Each directory on the path is synced, from the directory itself up to the root, so a deeper path takes longer to
  /// create. For a relative path, the default implementation syncs the directories up to the current directory. The
  /// local file system makes the path full first, so it syncs the directories above the current directory too. A
  /// directory that the process may not open, such as a home directory that other users may only pass through, is
  /// skipped, because no sync can reach it.
  /// </para>
  /// <para>
  /// Windows has no documented way to sync a directory. On Windows, the names of new directories therefore reach the
  /// disk on the file system's own schedule.
  /// </para>
  /// </remarks>
  void CreateDirectory(string path);
}
