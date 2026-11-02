namespace ZoneTree.WAL;

// Lifecycle notifications connect the journal with durable LSM publication.
internal interface ISharedWriteAheadLogCoordinator
{
  Task SyncAsync();

  void SyncBeforeMerge();

  void CompleteRecovery(IReadOnlyCollection<long> liveSegments);
}
