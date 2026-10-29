namespace ZoneTree.Exceptions;

/// <summary>
/// Thrown by writes after the write ahead log of a moved mutable segment failed to
/// sync while <see cref="Options.ZoneTreeOptions{TKey, TValue}.PreserveWriteOrderAcrossSegments"/>
/// was set. Later writes could reach the disk before the unsynced ones, so the tree
/// stops accepting writes. Reads keep working. Reopen the tree to write again.
/// </summary>
public sealed class WriteOrderBarrierFailedException : ZoneTreeException
{
  public WriteOrderBarrierFailedException(Exception syncFailure)
      : base("The write ahead log of the previous mutable segment failed to sync. " +
             "Writes are stopped to keep the write order; reopen the tree to write again.",
             syncFailure)
  {
  }
}
