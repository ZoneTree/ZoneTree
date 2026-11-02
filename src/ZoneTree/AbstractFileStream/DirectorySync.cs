using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ZoneTree.AbstractFileStream;

/// <summary>POSIX namespace durability, separate from flushing file contents.</summary>
internal static class DirectorySync
{
  const int Interrupted = 4; // EINTR on Linux, macOS and FreeBSD.

  internal static void Flush(string path)
  {
    // Windows has no POSIX directory-fsync operation. Its file flush and
    // namespace APIs retain their existing filesystem durability semantics.
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
        throw Failure(directory, error);
      while (Sync(handle) != 0)
      {
        error = Marshal.GetLastPInvokeError();
        if (error != Interrupted)
          throw Failure(directory, error);
      }
    }
  }

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
