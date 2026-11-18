namespace ZoneTree.AbstractFileStream;

/// <summary>
/// Retries a file operation that failed because another process has the file open, with longer waits each time.
/// </summary>
/// <remarks>
/// On Windows, an operation on a file can fail while another process, such as a virus scanner or a search indexer, has
/// that file open. These programs usually close a new or changed file within a moment, or within a few seconds for a
/// large file, so waiting and trying again usually succeeds. If the file stays open for longer, or the operation fails
/// for any other reason, the error is reported.
/// <para>
/// Linux and macOS never block a rename because another process has a file open. On those systems, .NET reports file
/// errors with Unix error numbers, which never match the Windows error codes checked here, so nothing is retried.
/// </para>
/// </remarks>
internal static class HeldFileRetry
{
  /// <summary>How long to wait before each retry, in milliseconds; about 3.9 seconds in total.</summary>
  internal static readonly int[] DelaysMilliseconds = [10, 25, 50, 100, 200, 500, 1000, 2000];

  // The Windows error codes that mean another process has the file open. Besides the sharing and lock violations,
  // ReplaceFile has two codes of its own: it could not remove the replaced file, or could not move the replacement into
  // place. With a backup, which is the only way ReplaceFile is called here, both leave every file under its original
  // name, so a retry is safe. Its third code, 1177, means the replaced file already has the backup name, so it is not
  // retried.
  const int SharingViolation = 32;
  const int LockViolation = 33;
  const int UnableToRemoveReplaced = 1175;
  const int UnableToMoveReplacement = 1176;

  /// <summary>The upper half of an HRESULT whose lower half is a Windows error code.</summary>
  const uint Win32Facility = 0x8007_0000;

  /// <summary>Returns true if a Windows error code means that another process has the file open.</summary>
  internal static bool IsHeld(int error)
      => error is SharingViolation or LockViolation or UnableToRemoveReplaced or UnableToMoveReplacement;

  /// <summary>Returns true if a failed file operation threw because another process has the file open.</summary>
  internal static bool IsHeld(Exception exception)
  {
    var result = unchecked((uint)exception.HResult);
    return (result & 0xFFFF_0000) == Win32Facility && IsHeld((int)(result & 0xFFFF));
  }

  /// <summary>
  /// Runs a file operation. While it throws an <see cref="IOException"/> because another process has the file open,
  /// it waits and runs it again, once after each wait in <see cref="DelaysMilliseconds"/>. Any other failure, and a
  /// file that is still open after the last wait, is thrown to the caller.
  /// </summary>
  /// <param name="operation">The operation, which must be safe to run again after it failed.</param>
  /// <param name="sleep">Waits the given number of milliseconds. Tests pass their own to avoid waiting.</param>
  internal static void Run(Action operation, Action<int> sleep)
  {
    for (var attempt = 0; ; ++attempt)
    {
      try
      {
        operation();
        return;
      }
      catch (IOException exception) when (attempt < DelaysMilliseconds.Length && IsHeld(exception))
      {
        sleep(DelaysMilliseconds[attempt]);
      }
    }
  }
}
