namespace ZoneTree.Cluster;

/// <summary>Opens the storage of a coordinated column from a configured ZoneTree factory.</summary>
internal static class ClusterMvccZoneTreeFactory
{
  internal static ClusterMvccZoneTree<TKey, TValue> Open<TKey, TValue>(
      ZoneTreeFactory<TKey, TValue> factory, bool canCreate, bool canOpen)
  {
    factory.FillMissingOptionsForKnownTypes();
    factory.InitWriteAheadLogProvider(isTransactional: true);
    return ClusterMvccZoneTree<TKey, TValue>.Load(factory.Options, canCreate, canOpen);
  }
}
