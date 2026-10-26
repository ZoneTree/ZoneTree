namespace ZoneTree.WAL;

/// <summary>
/// Lets concurrent callers share disk syncs.
/// One caller syncs at a time. The others wait for it,
/// and skip their sync when it covered what they wrote.
/// </summary>
/// <remarks>
/// Callers measure their writes with a count that only grows,
/// such as written bytes or appended records. Setting the synced count
/// lower, for example after a truncation, makes the next caller sync.
/// </remarks>
internal sealed class DiskSyncGroup
{
  readonly object SyncRoot = new();

  bool IsSyncRunning;

  long syncedCount;

  /// <summary>
  /// The count of writes that are synced to the disk.
  /// </summary>
  public long SyncedCount
  {
    get => Volatile.Read(ref syncedCount);
    set => Volatile.Write(ref syncedCount, value);
  }

  /// <summary>
  /// Waits for the running sync.
  /// </summary>
  /// <param name="targetCount">The count of writes made before the call.</param>
  /// <returns>
  /// false if a sync covered the target count.
  /// Otherwise true, and the caller syncs and then calls <see cref="Exit"/>.
  /// </returns>
  public bool Enter(long targetCount)
  {
    lock (SyncRoot)
    {
      // Checking the count after each wait lets every caller that a sync
      // covered return, even when the next sync has already started.
      while (IsSyncRunning && SyncedCount < targetCount)
        Monitor.Wait(SyncRoot);
      if (SyncedCount >= targetCount)
        return false;
      IsSyncRunning = true;
      return true;
    }
  }

  public void Exit()
  {
    lock (SyncRoot)
    {
      IsSyncRunning = false;
      Monitor.PulseAll(SyncRoot);
    }
  }
}
