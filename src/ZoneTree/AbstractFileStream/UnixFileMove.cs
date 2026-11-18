using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ZoneTree.AbstractFileStream;

/// <summary>
/// Renames files on Linux and macOS with the system's own atomic renames, where .NET's <c>File.Move</c> is not atomic.
/// </summary>
/// <remarks>
/// <c>File.Move</c> falls short in two cases. When it must not overwrite, it checks that the destination is free and
/// then renames the file, so it replaces a file that another process creates in between. When it may overwrite and the
/// paths are on different devices, it copies the file and deletes the original, which a crash can interrupt.
/// <para>
/// A move that must not replace a file uses the system's own rename that refuses to replace one: <c>renameat2</c> with
/// <c>RENAME_NOREPLACE</c> on Linux, and <c>renamex_np</c> with <c>RENAME_EXCL</c> on macOS. It checks the destination
/// and renames the file in one atomic step.
/// </para>
/// <para>
/// Some systems lack that rename: other systems such as FreeBSD, Linux with a C library older than glibc 2.28 or a
/// kernel older than 3.15, and file systems that do not support it, such as NFS. What a move does there is chosen by
/// its caller with <see cref="NoReplaceFallback"/>.
/// </para>
/// <para>
/// Every rename here renames a symbolic link itself, never the file that it points to.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static partial class UnixFileMove
{
  const int Success = 0;

  /// <summary>
  /// The result that means the system has no rename that refuses to replace a file. Unix error numbers are never
  /// negative, so it cannot be mistaken for one.
  /// </summary>
  const int Unavailable = -1;

  // Unix error numbers that are the same on Linux, macOS and FreeBSD.
  const int NotPermitted = 1; // EPERM: also what some container sandboxes return for a system call that they block.
  const int NoSuchEntry = 2; // ENOENT
  const int Interrupted = 4; // EINTR: a signal interrupted the call, which can simply be repeated.
  const int AccessDenied = 13; // EACCES
  const int AlreadyExists = 17; // EEXIST: a file already exists at the destination.
  const int InvalidArgument = 22; // EINVAL: also what a file system returns for a rename option that it lacks.

  // Linux error numbers.
  const int LinuxNoSuchCall = 38; // ENOSYS: the kernel is older than the call.
  const int LinuxNotSupported = 95; // EOPNOTSUPP, which Linux also names ENOTSUP.

  // macOS error numbers, which FreeBSD shares.
  const int MacNotSupported = 45; // ENOTSUP
  const int MacNoSuchCall = 78; // ENOSYS
  const int MacOperationNotSupported = 102; // EOPNOTSUPP

  /// <summary>
  /// Linux's AT_FDCWD, which makes renameat2 and linkat resolve a relative path against the current directory. macOS
  /// uses another value, which does no harm: every path passed here is full, and a full path ignores this argument.
  /// </summary>
  const int CurrentDirectory = -100;

  /// <summary>Linux's RENAME_NOREPLACE: tells renameat2 to fail instead of replacing a file.</summary>
  const uint NoReplace = 1;

  /// <summary>macOS's RENAME_EXCL: tells renamex_np to fail instead of replacing a file.</summary>
  const uint Exclusive = 4;

  /// <summary>True once the C library turned out to have no renameat2, so that it is not looked up again.</summary>
  static volatile bool RenameIsMissing;

  /// <summary>
  /// Moves a file to a new path in one atomic step, replacing any file there. A crash leaves the destination with
  /// either its old file or the new one. Unlike <c>File.Move</c>, it fails when the paths are on different devices
  /// instead of copying the file.
  /// </summary>
  internal static void MoveReplacing(string source, string destination)
  {
    var from = Path.GetFullPath(source);
    var to = Path.GetFullPath(destination);
    while (Rename(from, to) != 0)
    {
      var error = Marshal.GetLastPInvokeError();
      if (error != Interrupted)
        throw Failure(error, from, to);
    }
  }

  /// <summary>
  /// Moves a file to a new path, and fails if a file already exists there. Where the system has a rename that refuses
  /// to replace a file, the move is one atomic step, and a crash leaves the file under either its old name or its new
  /// name. Elsewhere, the move does what <paramref name="fallback"/> says.
  /// </summary>
  /// <exception cref="IOException">A file already exists at the destination.</exception>
  /// <exception cref="NotSupportedException">
  /// The system or the file system has no rename that refuses to replace a file, and the fallback is
  /// <see cref="NoReplaceFallback.Fail"/>, or it is <see cref="NoReplaceFallback.HardLink"/> and the file system has no
  /// hard links either.
  /// </exception>
  internal static void MoveWithoutReplacing(string source, string destination, NoReplaceFallback fallback)
  {
    var from = Path.GetFullPath(source);
    var to = Path.GetFullPath(destination);
    var error = RenameWithoutReplacing(from, to);
    if (error == Success)
      return;
    // EPERM may come from a container sandbox that blocks renameat2 rather than from the file's permissions, so a move
    // that has a fallback tries it. A real permission problem makes the fallback fail too.
    var mayBeUnavailable = IsUnavailable(error) || (error == NotPermitted && fallback != NoReplaceFallback.Fail);
    if (!mayBeUnavailable)
      throw Failure(error, from, to);
    switch (fallback)
    {
      case NoReplaceFallback.HardLink:
        LinkThenUnlink(from, to);
        return;
      case NoReplaceFallback.CheckFirst:
        File.Move(from, to, overwrite: false);
        return;
      default:
        throw new NotSupportedException(
            $"Could not move '{from}' to '{to}': the file system cannot rename a file in one atomic step without " +
            $"replacing an existing file. {Reason(error)}");
    }
  }

  /// <summary>
  /// Moves a file in two steps, for systems without a rename that refuses to replace a file. First, it gives the file a
  /// second name at the destination, called a hard link. The system creates this link in one atomic step, and fails if
  /// a file already exists at the destination. Second, it removes the file's old name.
  /// <para>
  /// A crash between the steps leaves the complete file under both names, so this move suits only a file whose old
  /// name nothing else uses. If the old name cannot be removed, the move removes the new name again and fails, so a
  /// move that fails without a crash never leaves the file under both names.
  /// </para>
  /// </summary>
  /// <exception cref="IOException">A file already exists at the destination.</exception>
  /// <exception cref="NotSupportedException">The file system has no hard links.</exception>
  internal static void MoveByLink(string source, string destination)
      => LinkThenUnlink(Path.GetFullPath(source), Path.GetFullPath(destination));

  /// <summary>The steps of <see cref="MoveByLink"/>, for paths that are already full.</summary>
  static void LinkThenUnlink(string from, string to)
  {
    while (Link(from, to) != 0)
    {
      var error = Marshal.GetLastPInvokeError();
      if (error == Interrupted)
        continue;
      if (HasNoHardLinks(error))
        throw new NotSupportedException(
            $"Could not move '{from}' to '{to}': the file system has neither a rename that refuses to replace a file " +
            $"nor hard links. {Reason(error)}");
      throw Failure(error, from, to);
    }
    try
    {
      File.Delete(from);
    }
    catch
    {
      // Undo the link, so that a failed move leaves the file under its old name only. If the old name is gone after
      // all, the link is the file's only name and stays.
      if (File.Exists(from))
        RemoveLink(to);
      throw;
    }
  }

  /// <summary>
  /// Tries the system's rename that refuses to replace a file, repeating it if a signal interrupts it. Returns zero if
  /// the file was renamed, and otherwise the Unix error number, or <see cref="Unavailable"/> if the system has no such
  /// rename.
  /// </summary>
  internal static int RenameWithoutReplacing(string from, string to)
  {
    int error;
    do
      error = TryRenameWithoutReplacing(from, to);
    while (error == Interrupted);
    return error;
  }

  static int TryRenameWithoutReplacing(string from, string to)
  {
    if (RenameIsMissing)
      return Unavailable;
    try
    {
      int result;
      if (OperatingSystem.IsLinux())
        result = RenameAt2(CurrentDirectory, from, CurrentDirectory, to, NoReplace);
      else if (OperatingSystem.IsMacOS())
        result = RenameExclusive(from, to, Exclusive);
      else
        return Unavailable;
      return result == 0 ? Success : Marshal.GetLastPInvokeError();
    }
    catch (EntryPointNotFoundException)
    {
      // The C library is older than the rename. glibc, for example, added renameat2 in version 2.28.
      RenameIsMissing = true;
      return Unavailable;
    }
  }

  /// <summary>
  /// Returns true if a failed rename means that this system or this file system has no rename that refuses to replace
  /// a file.
  /// </summary>
  static bool IsUnavailable(int error)
  {
    if (error is Unavailable or InvalidArgument)
      return true;
    return IsNotSupported(error);
  }

  /// <summary>Returns true if a failed link means that the file system has no hard links.</summary>
  static bool HasNoHardLinks(int error) => error == NotPermitted || IsNotSupported(error);

  /// <summary>Returns true if the error says that the system or the file system does not support the call.</summary>
  static bool IsNotSupported(int error)
      => OperatingSystem.IsLinux()
          ? error is LinuxNoSuchCall or LinuxNotSupported
          : error is MacNotSupported or MacNoSuchCall or MacOperationNotSupported;

  /// <summary>Returns the exception for a failed move, of the same type that .NET's own file methods throw.</summary>
  static Exception Failure(int error, string from, string to)
  {
    if (error == AlreadyExists)
      return new IOException($"The file '{to}' already exists.", AlreadyExists);
    var message = $"Could not move '{from}' to '{to}': {NativeError.Message(error)}";
    return error switch
    {
      // A missing entry is either the file itself or the directory of its new path.
      NoSuchEntry when !File.Exists(from) => new FileNotFoundException(message, from),
      NoSuchEntry => new DirectoryNotFoundException(message),
      NotPermitted or AccessDenied => new UnauthorizedAccessException(message),
      _ => new IOException(message, error)
    };
  }

  static string Reason(int error) => error == Unavailable ? "This system has no such rename." : NativeError.Message(error);

  /// <summary>Removes a link that a failed move made, and leaves it in place if even that fails.</summary>
  static void RemoveLink(string path)
  {
    try
    {
      File.Delete(path);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
  }

  /// <summary>
  /// Gives an existing file a second name, called a hard link. Returns zero on success. On failure, returns -1, and
  /// <see cref="Marshal.GetLastPInvokeError"/> returns the Unix error number.
  /// </summary>
  /// <remarks>
  /// linkat without flags links a symbolic link itself. Whether plain link follows it is left to each system by POSIX.
  /// </remarks>
  internal static int Link(string existingPath, string newPath)
      => LinkAt(CurrentDirectory, existingPath, CurrentDirectory, newPath, 0);

#if NET7_0_OR_GREATER
  /// <summary>
  /// The system's rename, which replaces any file at the new path in one atomic step. Returns zero on success. On
  /// failure, returns -1, and <see cref="Marshal.GetLastPInvokeError"/> returns the Unix error number.
  /// </summary>
  [LibraryImport("libc", EntryPoint = "rename", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
  private static partial int Rename(string oldPath, string newPath);

  [LibraryImport("libc", EntryPoint = "linkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
  private static partial int LinkAt(int oldDirectory, string oldPath, int newDirectory, string newPath, int flags);

  /// <summary>
  /// Linux's renameat2. Returns zero on success. On failure, returns -1, and
  /// <see cref="Marshal.GetLastPInvokeError"/> returns the Unix error number.
  /// </summary>
  [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
  private static partial int RenameAt2(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);

  /// <summary>
  /// macOS's renamex_np. Returns zero on success. On failure, returns -1, and
  /// <see cref="Marshal.GetLastPInvokeError"/> returns the Unix error number.
  /// </summary>
  [LibraryImport("libc", EntryPoint = "renamex_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
  private static partial int RenameExclusive(string from, string to, uint flags);
#else
#pragma warning disable CA2101 // POSIX paths are UTF-8; LPUTF8Str explicitly supplies that encoding.
  [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
  static extern int Rename([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

  [DllImport("libc", EntryPoint = "linkat", SetLastError = true)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
  static extern int LinkAt(int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, int newDirectory,
      [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, int flags);

  [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
  static extern int RenameAt2(int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, int newDirectory,
      [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);

  [DllImport("libc", EntryPoint = "renamex_np", SetLastError = true)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
  static extern int RenameExclusive([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to,
      uint flags);
#pragma warning restore CA2101
#endif
}
