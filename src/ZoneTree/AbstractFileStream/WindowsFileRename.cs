using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ZoneTree.AbstractFileStream;

/// <summary>
/// Renames files on Windows. Unlike .NET's <c>File.Move</c>, a rename can replace a file that other programs are
/// reading, and can be written to disk before it returns.
/// </summary>
/// <remarks>
/// A rename asks for POSIX semantics, as on Linux: the replaced file leaves the directory at once, and programs that
/// have it open keep reading its old content. <c>File.Move</c> refuses to replace a file that is open.
/// <para>
/// A rename changes the file's metadata, and Windows writes metadata changes to disk at once only for a file opened
/// with write-through, which is how a durable rename opens it. This is the only way to make a rename durable here,
/// because the directory sync does nothing on Windows. Relying on the order of later changes is not enough: NTFS keeps
/// metadata changes in order within one volume, but not across volumes, such as a WAL directory on another drive, and
/// file systems without a journal, such as FAT, keep no order at all.
/// </para>
/// <para>
/// Like every rename, it keeps the file's own access rules: a file that replaces another one does not take over the
/// replaced file's rules, as it would with <c>File.Replace</c>. A symbolic link is renamed itself, as <c>File.Move</c>
/// renames it, never the file that it points to.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class WindowsFileRename
{
  const uint DeleteAccess = 0x0001_0000;
  const uint ShareAll = 0x7;
  const uint OpenExisting = 3;
  const uint WriteThrough = 0x8000_0000;

  /// <summary>
  /// FILE_FLAG_OPEN_REPARSE_POINT: opens a symbolic link itself instead of the file that it points to.
  /// </summary>
  const uint OpenLinkItself = 0x0020_0000;

  const int RenameInformation = 3;
  const int RenameInformationEx = 22;
  const uint ReplaceIfExists = 0x1;
  const uint PosixSemantics = 0x2;

  const int Success = 0;
  const int InvalidFunction = 1;
  const int FileNotFound = 2;
  const int PathNotFound = 3;
  const int AccessDenied = 5;
  const int NotSupported = 50;
  const int InvalidParameter = 87;

  /// <summary>
  /// Renames a file in one atomic step. If another process has the file or the destination open in a way that blocks
  /// the rename, the rename is retried for a few seconds.
  /// </summary>
  /// <param name="source">The current path of the file.</param>
  /// <param name="destination">The new path of the file.</param>
  /// <param name="replace">
  /// True to replace a file that already exists at the destination; false to fail instead.
  /// </param>
  /// <param name="writeThrough">True to write the rename to disk before this method returns.</param>
  /// <remarks>
  /// On file systems that support POSIX semantics, such as NTFS, the rename can replace a file that other programs are
  /// reading. On other file systems, the rename falls back to the classic Windows rename, which fails while the file it
  /// would replace is open.
  /// </remarks>
  internal static void Rename(string source, string destination, bool replace, bool writeThrough)
  {
    var path = Extended(source);
    var information = RenameTo(Extended(destination));
    HeldFileRetry.Run(() =>
    {
      var error = TryRename(path, information, replace, writeThrough);
      if (error != Success)
        throw Failure(error, source, destination);
    }, Thread.Sleep);
  }

  /// <summary>
  /// Tries the rename once. Returns zero if the file was renamed, and otherwise the Windows error code of the failure.
  /// </summary>
  /// <remarks>
  /// The file is opened with delete access only, which is all a rename needs. A handle with only delete access does not
  /// get in the way of programs that read the file and allow it to be deleted. With <paramref name="writeThrough"/>,
  /// the handle is opened with write-through, so the rename made through it goes to disk immediately.
  /// </remarks>
  static int TryRename(string path, byte[] information, bool replace, bool writeThrough)
  {
    using var file = CreateFile(path, DeleteAccess, ShareAll, 0, OpenExisting,
        OpenLinkItself | (writeThrough ? WriteThrough : 0), 0);
    if (file.IsInvalid)
      return Marshal.GetLastPInvokeError();
    SetFlags(information, (replace ? ReplaceIfExists : 0) | PosixSemantics);
    if (SetFileInformationByHandle(file, RenameInformationEx, information, (uint)information.Length))
      return Success;
    var error = Marshal.GetLastPInvokeError();
    if (error is not (InvalidParameter or NotSupported or InvalidFunction))
      return error;
    // Older versions of Windows, and file systems without POSIX semantics, reject the POSIX rename as an unknown
    // request. The classic rename is tried instead. Its first field is a single true or false value that says whether
    // to replace a file that exists at the destination.
    SetFlags(information, replace ? 1u : 0u);
    return SetFileInformationByHandle(file, RenameInformation, information, (uint)information.Length)
        ? Success
        : Marshal.GetLastPInvokeError();
  }

  /// <summary>
  /// Builds the FILE_RENAME_INFO structure that tells Windows how to rename the file. It holds, in order: the rename
  /// flags, which <see cref="SetFlags"/> fills in before each call; a handle to a root directory, left empty because
  /// the new name is a full path; the length of the new name in bytes; and the new name itself, ending with a null
  /// character. The flags field takes the width of a pointer, because the handle that follows it must start at a
  /// pointer-aligned position.
  /// </summary>
  static byte[] RenameTo(string name)
  {
    var lengthOffset = 2 * IntPtr.Size;
    var nameOffset = lengthOffset + sizeof(uint);
    var nameLength = name.Length * sizeof(char);
    var information = new byte[nameOffset + nameLength + sizeof(char)];
    BinaryPrimitives.WriteUInt32LittleEndian(information.AsSpan(lengthOffset), (uint)nameLength);
    MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(information.AsSpan(nameOffset));
    return information;
  }

  /// <summary>Writes the rename flags into the first field of a FILE_RENAME_INFO structure.</summary>
  static void SetFlags(byte[] information, uint flags) => BinaryPrimitives.WriteUInt32LittleEndian(information, flags);

  /// <summary>
  /// Returns the full path with the <c>\\?\</c> prefix, which lifts the 260-character limit of classic Windows paths.
  /// </summary>
  static string Extended(string path)
  {
    var full = Path.GetFullPath(path);
    if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
      return full;
    return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
  }

  /// <summary>Returns the exception for a failed rename, of the same type that .NET's own file methods throw.</summary>
  static Exception Failure(int error, string source, string destination)
  {
    var message = $"Could not rename '{source}' to '{destination}': {NativeError.Message(error)}";
    return error switch
    {
      FileNotFound => new FileNotFoundException(message, source),
      PathNotFound => new DirectoryNotFoundException(message),
      AccessDenied => new UnauthorizedAccessException(message),
      _ => new IOException(message, unchecked((int)(0x8007_0000u | (uint)error)))
    };
  }

#if NET7_0_OR_GREATER
  [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags,
      nint template);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte[] information, uint size);
#else
  [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags,
      nint template);

  [DllImport("kernel32.dll", SetLastError = true)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte[] information, uint size);
#endif
}
