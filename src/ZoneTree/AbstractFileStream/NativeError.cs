using System.Runtime.InteropServices;

namespace ZoneTree.AbstractFileStream;

/// <summary>Describes the errors that the operating system's own file functions report.</summary>
internal static class NativeError
{
  /// <summary>
  /// Returns the system's description of an error: of a Windows error code on Windows, and of a Unix error number on
  /// Linux and macOS.
  /// </summary>
#if NET7_0_OR_GREATER
  internal static string Message(int error) => Marshal.GetPInvokeErrorMessage(error);
#else
  internal static string Message(int error) => new System.ComponentModel.Win32Exception(error).Message;
#endif
}
