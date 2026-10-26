namespace ZoneTree.Exceptions;

/// <summary>
/// The write ahead log could not sync its records to the disk.
/// Records appended to the write ahead log may not survive a power loss.
/// </summary>
public sealed class WriteAheadLogSyncFailedException : ZoneTreeException
{
  public string FilePath { get; }

  public WriteAheadLogSyncFailedException(string filePath, Exception innerException)
      : base($"Write ahead log {filePath} cannot sync its records to the disk.", innerException)
  {
    FilePath = filePath;
  }
}
