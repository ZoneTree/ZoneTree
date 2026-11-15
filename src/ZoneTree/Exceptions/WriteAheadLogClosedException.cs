namespace ZoneTree.Exceptions;

/// <summary>
/// A record was appended to a write ahead log that was already closed or dropped.
/// A tree writes to its write ahead log without a lock, so a write that races the
/// tree's dispose or drop gets this. Like a write to any closed write ahead log, it is
/// an <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class WriteAheadLogClosedException : ObjectDisposedException
{
  public string FilePath { get; }

  public WriteAheadLogClosedException(string filePath)
      : base(null, $"Write ahead log {filePath} is closed.")
  {
    FilePath = filePath;
  }
}
