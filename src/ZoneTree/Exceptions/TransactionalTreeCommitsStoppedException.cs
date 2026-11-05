namespace ZoneTree.Exceptions;

/// <summary>
/// Thrown by commits of a transactional tree after a commit sync of the tree failed.
/// The committing transaction is rolled back. Reads keep working. Reopen the tree
/// to commit again; recovery decides the outcome of the commits that were in doubt.
/// </summary>
public sealed class TransactionalTreeCommitsStoppedException : ZoneTreeException
{
  public TransactionalTreeCommitsStoppedException(Exception syncFailure)
      : base("A previous commit sync failed, so commits are stopped. Reopen the tree to commit again.",
             syncFailure)
  {
  }
}
