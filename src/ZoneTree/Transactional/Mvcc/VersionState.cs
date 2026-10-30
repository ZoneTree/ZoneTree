namespace ZoneTree.Transactional.Mvcc;

/// <summary>
/// The state of a stored version for garbage collection.
/// </summary>
internal enum VersionState : byte
{
  /// <summary>
  /// Committed and visible to every active and future snapshot.
  /// </summary>
  VisibleToAll,

  /// <summary>
  /// Has no entry: committed and visible to every active and future snapshot,
  /// unless the storage tree deleted it for a forgotten rollback.
  /// </summary>
  Unregistered,

  /// <summary>
  /// Committed after the oldest active snapshot.
  /// </summary>
  Committed,

  /// <summary>
  /// Written by an active transaction.
  /// </summary>
  Active,

  /// <summary>
  /// Written by a rolled back transaction.
  /// </summary>
  Aborted,
}
