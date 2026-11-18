using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ZoneTree.AbstractFileStream;

/// <summary>POSIX namespace durability, separate from flushing file contents.</summary>
internal static class DirectorySync
{
  // Unix error numbers that are the same on Linux, macOS and FreeBSD.
  const int NotPermitted = 1; // EPERM
  const int Interrupted = 4; // EINTR
  const int AccessDenied = 13; // EACCES

  static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

  /// <summary>
  /// Syncs a directory and every directory above it, so that the names that a recursive creation added are on disk.
  /// </summary>
  internal static void FlushTree(string path, Action<string> syncDirectory)
      // Recursive creation resolves "..", so the walk must too: the folder before a ".." may not exist.
      => SyncUpToTheRoot(WithoutDotSegments(Path.TrimEndingDirectorySeparator(path)), syncDirectory);

  /// <summary>
  /// Syncs a directory and every directory above it, up to the root, because each directory holds the name of the one
  /// below it. For a relative path, the current directory, which holds the name of the path's top directory, is synced
  /// last.
  /// </summary>
  /// <param name="path">A path without <c>.</c> or <c>..</c> segments and without a trailing separator.</param>
  /// <param name="syncDirectory">
  /// Syncs one directory, and throws <see cref="UnauthorizedAccessException"/> if the process may not open it.
  /// </param>
  /// <remarks>
  /// A directory that the process may not open is skipped, such as a home directory that other users may only pass
  /// through, or a directory outside what a sandbox allows. No sync can reach such a directory, and it was normally not
  /// created by this process, because a new directory is readable by its creator. So the only name left off the disk
  /// is one that this process put into a directory it may write to but not read. Any other failure is thrown.
  /// </remarks>
  internal static void SyncUpToTheRoot(string path, Action<string> syncDirectory)
  {
    for (var directory = path; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
      SyncIfPermitted(directory, syncDirectory);
    if (!Path.IsPathRooted(path) && path != ".")
      SyncIfPermitted(".", syncDirectory);
  }

  static void SyncIfPermitted(string directory, Action<string> syncDirectory)
  {
    try
    {
      syncDirectory(directory);
    }
    catch (UnauthorizedAccessException)
    {
      // Deliberately ignored: no sync can reach a directory the process may not open, so failing would only stop the
      // caller for nothing. PostgreSQL does the same: fsync_fname_ext in src/backend/storage/file/fd.c ignores EACCES
      // when it opens a directory to sync it. Do not turn this into a failure.
    }
  }

  /// <summary>
  /// Resolves the <c>.</c> and <c>..</c> segments of a path from its text alone, the way
  /// <c>Directory.CreateDirectory</c> resolves them before it creates anything. A path without such segments is
  /// returned exactly as it is.
  /// </summary>
  /// <remarks>
  /// Unlike <c>Path.GetFullPath</c>, this never makes a relative path full and never adds the current drive to a path
  /// such as <c>/data</c> on Windows, so it also suits providers that are not the local disk.
  /// <list type="bullet">
  /// <item>A rewritten path keeps the separator it uses, and loses repeated separators.</item>
  /// <item>A <c>..</c> at a root, such as <c>/</c>, <c>C:\</c> or <c>\\server\share</c>, stays at that root.</item>
  /// <item>
  /// A <c>..</c> at the start of a relative path is kept, and so is one right after a drive such as <c>D:</c>, which
  /// names the current directory on that drive rather than a root.
  /// </item>
  /// <item>
  /// A Windows path that starts with <c>\\?\</c> is returned unchanged, because Windows takes it literally.
  /// </item>
  /// </list>
  /// </remarks>
  internal static string WithoutDotSegments(string path)
  {
    if (OperatingSystem.IsWindows() && path.StartsWith(@"\\?\", StringComparison.Ordinal))
      return path;
    var root = path[..(Path.GetPathRoot(path)?.Length ?? 0)];
    var rest = path[root.Length..];
    var segments = rest.Split(Separators);
    if (Array.TrueForAll(segments, segment => segment is not ("." or "..")))
      return path;
    var separatorAt = rest.IndexOfAny(Separators);
    var separator = separatorAt >= 0 ? rest[separatorAt] : Path.DirectorySeparatorChar;
    // "D:" without a separator is the current directory on drive D, so a ".." after it is a real step up.
    var startsAtARoot = root.Length > 0 && !(OperatingSystem.IsWindows() && root.Length == 2 && root[1] == ':');
    var resolved = new List<string>();
    foreach (var segment in segments)
    {
      if (segment.Length == 0 || segment == ".")
        continue;
      if (segment == "..")
      {
        if (resolved.Count > 0 && resolved[^1] != "..")
        {
          resolved.RemoveAt(resolved.Count - 1);
          continue;
        }
        if (startsAtARoot)
          continue;
      }
      resolved.Add(segment);
    }
    if (resolved.Count == 0)
      return root.Length > 0 ? root : ".";
    // "C:\" and "/" end with a separator. "\\server\share" does not, so its separator follows it. "D:" gets none.
    var afterRoot = root.Length > 0 && Array.IndexOf(Separators, root[^1]) < 0 && separatorAt == 0
        ? separator.ToString()
        : "";
    return root + afterRoot + string.Join(separator, resolved);
  }

  /// <summary>Writes the names stored in a directory to disk. Does nothing on Windows.</summary>
  /// <exception cref="UnauthorizedAccessException">The process may not open the directory for reading.</exception>
  /// <exception cref="IOException">The directory could not be synced.</exception>
  internal static void Flush(string path)
  {
    // Windows has no documented way to sync a directory. NTFS accepts FlushFileBuffers on a directory opened for
    // writing, but what that guarantees for the directory's names is undocumented, so it is not relied on. Renames that
    // must be durable on Windows are written through instead; see WindowsFileRename.
    if (OperatingSystem.IsWindows())
      return;
    if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
      throw new PlatformNotSupportedException("Directory sync is not supported on this platform.");

    // open(O_RDONLY) also works for directories. Avoid OS-specific numeric
    // values for O_DIRECTORY/O_CLOEXEC and always close the descriptor.
    var directory = Path.GetFullPath(path);
    SafeFileHandle handle;
    int error;
    do
    {
      handle = Open(directory, 0);
      error = Marshal.GetLastPInvokeError();
      if (!handle.IsInvalid)
        break;
      handle.Dispose();
    } while (error == Interrupted);
    using (handle)
    {
      if (handle.IsInvalid)
        throw OpenFailure(directory, error);
      while (Sync(handle) != 0)
      {
        error = Marshal.GetLastPInvokeError();
        if (error != Interrupted)
          throw Failure(directory, error);
      }
    }
  }

  /// <summary>
  /// Returns the exception for a directory that could not be opened. A missing permission gets the type that .NET's
  /// own file methods throw for it, so that callers can tell it apart from a failed sync.
  /// </summary>
  static Exception OpenFailure(string path, int error) => error is NotPermitted or AccessDenied
      ? new UnauthorizedAccessException($"Could not open directory '{path}' to sync it.", new Win32Exception(error))
      : Failure(path, error);

  static IOException Failure(string path, int error) =>
      new($"Could not sync directory '{path}'.", new Win32Exception(error));

#pragma warning disable SYSLIB1054 // DllImport also supports the .NET 6 target.
#pragma warning disable CA2101 // POSIX paths are UTF-8; LPUTF8Str explicitly supplies that encoding.
  [DllImport("libc", EntryPoint = "open", SetLastError = true)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
  static extern SafeFileHandle Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
#pragma warning restore CA2101

  [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
  static extern int Sync(SafeFileHandle handle);
#pragma warning restore SYSLIB1054
}
