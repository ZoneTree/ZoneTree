namespace ZoneTree.Exceptions.WAL;

public sealed class IncompleteTailRecordFoundException : ZoneTreeException
{
  public long FileLength { get; set; }

  public long RecordPosition { get; set; }

  public int RecordIndex { get; set; }

  public long TruncationAmount => FileLength - RecordPosition;

  public IncompleteTailRecordFoundException(Exception innerException)
      : base($"Incomplete tail record found. {innerException.Message}", innerException)
  {
  }
}
