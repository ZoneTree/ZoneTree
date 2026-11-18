namespace ZoneTree.AbstractFileStream;

/// <summary>
/// Renames files on the local disk in one atomic step, with the best method that each operating system offers.
/// </summary>
/// <remarks>
/// On Windows, a rename can replace a file that other programs are reading. If another program has a file open in a
/// way that blocks the rename, the rename waits a few seconds for it to close the file.
/// <para>
/// On Linux and macOS, a rename that replaces a file is the system's own rename, which is atomic. A rename that must
/// not replace a file is the system's rename that refuses to replace one, which is atomic too. It therefore never
/// replaces a file that another process creates at the same moment. Where the system or the file system lacks that
/// rename, the caller chooses what happens with <see cref="NoReplaceFallback"/>.
/// </para>
/// <para>
/// On every system, a renamed file keeps its own permissions. A file that replaces another one does not take over the
/// replaced file's permissions, so permissions are best set on the directory, whose rules new files inherit.
/// </para>
/// <para>
/// Both paths must be on the same volume. A rename between volumes fails.
/// </para>
/// </remarks>
internal static class LocalFileRename
{
  /// <summary>
  /// Renames a file in one atomic step, so a crash leaves the file under either its old name or its new name. Where
  /// the system cannot rename atomically without replacing a file, <paramref name="fallback"/> decides instead.
  /// </summary>
  /// <param name="sourcePath">The current path of the file.</param>
  /// <param name="destinationPath">The new path of the file.</param>
  /// <param name="replace">
  /// True to replace a file that already exists at the destination; false to fail with an <see cref="IOException"/>
  /// if a file already exists there.
  /// </param>
  /// <param name="writeThrough">
  /// On Windows, true writes the rename to disk before this method returns. Linux and macOS have no such option. There,
  /// the rename reaches the disk when the caller syncs the directory.
  /// </param>
  /// <param name="fallback">
  /// What a rename that must not replace a file does on Linux and macOS where the system or the file system lacks a
  /// rename that refuses to replace one.
  /// </param>
  /// <exception cref="NotSupportedException">
  /// The rename must not replace a file, the system cannot make it in one atomic step, and the fallback is
  /// <see cref="NoReplaceFallback.Fail"/>, or <see cref="NoReplaceFallback.HardLink"/> on a file system without hard
  /// links.
  /// </exception>
  internal static void Rename(string sourcePath, string destinationPath, bool replace, bool writeThrough,
      NoReplaceFallback fallback = NoReplaceFallback.Fail)
  {
    if (OperatingSystem.IsWindows())
      WindowsFileRename.Rename(sourcePath, destinationPath, replace, writeThrough);
    else if (replace)
      UnixFileMove.MoveReplacing(sourcePath, destinationPath);
    else
      UnixFileMove.MoveWithoutReplacing(sourcePath, destinationPath, fallback);
  }
}

/// <summary>
/// What a rename that must not replace a file does on Linux and macOS where the system or the file system has no
/// rename that refuses to replace a file in one atomic step. <see cref="UnixFileMove"/> lists those systems. Each
/// choice gives up something different: the move itself, a single name after a crash, or protection from a race.
/// </summary>
internal enum NoReplaceFallback
{
  /// <summary>
  /// Fail with <see cref="NotSupportedException"/>. For a rename that promises both that it never replaces a file and
  /// that a crash leaves the file under exactly one name.
  /// </summary>
  Fail,

  /// <summary>
  /// Give the file its new name as a hard link, which the system refuses if a file already exists there, and then
  /// remove its old name. It never replaces a file, but a crash between the two steps leaves the file under both names.
  /// Only for a file whose old name is never written to again, such as the random name of a temporary file.
  /// </summary>
  HardLink,

  /// <summary>
  /// Use <c>File.Move</c>, which checks that the destination is free and then renames the file. A crash leaves the file
  /// under exactly one name, but a file that another process creates at the destination between the two steps is
  /// replaced. For a rename whose old name may be used again, where no other process creates the same file.
  /// </summary>
  CheckFirst
}
