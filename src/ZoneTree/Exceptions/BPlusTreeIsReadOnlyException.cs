namespace ZoneTree.Exceptions;

public sealed class BPlusTreeIsReadOnlyException : ZoneTreeException
{
  public BPlusTreeIsReadOnlyException()
      : base("Can't write to read-only BPlusTree.")
  {
  }
}
