namespace ZoneTree;

/// <summary>
/// Provides functionality to manage merge operations and memory compaction within the ZoneTree.
/// </summary>
/// <remarks>
/// Disposal waits for tracked background merge threads to finish. Call
/// <see cref="TryCancelBackgroundThreads"/> before disposal to request
/// cancellation instead of waiting for the current work to complete normally.
/// </remarks>
public interface IMaintainer : IDisposable
{
  /// <summary>
  /// The threshold of record count in read-only segments that triggers the start of a merge operation.
  /// The default value is 0.
  /// </summary>
  int MergeThreshold { get; set; }

  /// <summary>
  /// The maximum number of read-only segments allowed before triggering a merge operation.
  /// The default value is 64.
  /// </summary>
  int MaximumReadOnlySegmentCount { get; set; }

  /// <summary>
  /// Gets or sets a value indicating whether a periodic cleanup job is enabled to release 
  /// unused block and key/value caches in disk segments.
  /// Toggling this property starts or stops the associated periodic timer.
  /// Maintainers created with <c>zoneTree.CreateMaintainer()</c> start this job by default.
  /// </summary>
  bool EnableInactiveCacheCleanup { get; set; }

  /// <summary>
  /// Gets or sets the lifespan of the disk segment block cache.
  /// The default value is 1 minute.
  /// </summary>
  TimeSpan BlockCacheLifetime { get; set; }

  /// <summary>
  /// Gets or sets the interval at which the cleanup job runs to remove inactive
  /// block buffers and expired circular key/value cache records.
  /// The default value is 30 seconds.
  /// </summary>
  TimeSpan InactiveBlockCacheCleanupInterval { get; set; }

  /// <summary>
  /// Attempts to cancel all background threads associated with the maintainer.
  /// </summary>
  void TryCancelBackgroundThreads();

  /// <summary>
  /// Blocks the calling thread until all background threads have finished execution.
  /// </summary>
  /// <remarks>
  /// Disposal also waits for background threads. Call this method when merge
  /// completion must be observed before the maintainer's lifetime ends.
  /// </remarks>
  void WaitForBackgroundThreads();

  /// <summary>
  /// Asynchronously waits for the completion of all background threads.
  /// </summary>
  /// <remarks>
  /// Use this method when merge completion must be observed asynchronously
  /// before the maintainer's lifetime ends.
  /// </remarks>
  /// <returns>A task representing the asynchronous operation.</returns>
  Task WaitForBackgroundThreadsAsync();

  /// <summary>
  /// Evicts in-memory data to disk by advancing the mutable segment and initiating a merge process.
  /// </summary>
  /// <remarks>
  /// This operation is crucial for freeing up memory by transferring data from the mutable in-memory segment 
  /// to disk storage. It advances the current mutable segment, ensuring all in-memory data is properly prepared 
  /// for disk storage, and then initiates the merging process to maintain the efficiency and integrity of the LSM tree.
  /// </remarks>
  void EvictToDisk();

  /// <summary>
  /// Starts the merge process in a new background thread.
  /// </summary>
  void StartMerge();

  /// <summary>
  /// Initiates a merge operation for selected bottom segments, combining them into a single bottom disk segment.
  /// </summary>
  /// <param name="fromIndex">The starting index of the range of segments to merge.</param>
  /// <param name="toIndex">The ending index of the range of segments to merge.</param>
  void StartBottomSegmentsMerge(int fromIndex = 0, int toIndex = int.MaxValue);
}
