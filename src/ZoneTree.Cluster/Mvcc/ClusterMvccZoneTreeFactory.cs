namespace ZoneTree.Cluster;

/// <summary>Opens the storage of a coordinated column from a configured ZoneTree factory.</summary>
internal static class ClusterMvccZoneTreeFactory
{
  /// <param name="factory">The configured factory of the column's storage.</param>
  /// <param name="canCreate">True if the storage may not exist yet.</param>
  /// <param name="canOpen">True if existing storage may be opened.</param>
  /// <param name="coordinator">The coordinator of the column's domain.</param>
  /// <param name="isRolledBack">
  /// Tells whether recovery rolls back the commit at a timestamp: the domain's commits that
  /// were not durable when an earlier process stopped uncleanly.
  /// </param>
  internal static ClusterMvccZoneTree<TKey, TValue> Open<TKey, TValue>(
      ZoneTreeFactory<TKey, TValue> factory, bool canCreate, bool canOpen,
      DomainCoordinator coordinator, Func<long, bool> isRolledBack)
  {
    factory.FillMissingOptionsForKnownTypes();
    factory.InitWriteAheadLogProvider(isTransactional: true);
    return ClusterMvccZoneTree<TKey, TValue>.Load(factory.Options, canCreate, canOpen, coordinator, isRolledBack);
  }

  /// <summary>Restores a backup generation of the storage of a coordinated column.</summary>
  internal static Task RestoreAsync<TKey, TValue>(ZoneTreeFactory<TKey, TValue> factory,
      Backup.ILiveBackupSource source, long generationId)
  {
    factory.FillMissingOptionsForKnownTypes();
    factory.InitWriteAheadLogProvider(isTransactional: true);
    return ClusterMvccZoneTree<TKey, TValue>.RestoreStorageAsync(factory.Options, source, generationId);
  }
}
