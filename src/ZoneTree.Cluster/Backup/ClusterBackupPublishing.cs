using ZoneTree.Backup;
using ZoneTree.Segments.Disk;

namespace ZoneTree.Cluster;

/// <summary>
/// Lets the columns of a cluster backup generation publish their generations only once every
/// one of them has uploaded its records and files. A failed column keeps the others from
/// publishing, so a failed cluster generation leaves no published column generations, which
/// would count against the stores' retention.
/// </summary>
internal sealed class ClusterBackupPublishing
{
  readonly TaskCompletionSource<bool> Decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
  int remaining;

  internal ClusterBackupPublishing(int columns)
  {
    remaining = columns;
    if (columns == 0)
      Decision.TrySetResult(true);
  }

  /// <summary>Waits until every column arrived, or one failed. Returns true if all arrived.</summary>
  internal Task<bool> ArriveAsync()
  {
    if (Interlocked.Decrement(ref remaining) == 0)
      Decision.TrySetResult(true);
    return Decision.Task;
  }

  /// <summary>A column failed before it could publish.</summary>
  internal void Fail() => Decision.TrySetResult(false);

  /// <summary>Wraps a column's store so that its publish waits for the others.</summary>
  internal ILiveBackupStore Wrap(ILiveBackupStore store) => new Store(store, this);

  sealed class Store(ILiveBackupStore inner, ClusterBackupPublishing publishing) : ILiveBackupStore
  {
    public Task<long> GetNextGenerationIdAsync(CancellationToken cancellationToken)
        => inner.GetNextGenerationIdAsync(cancellationToken);

    public Task BeginGenerationAsync(long generationId, DateTime startedAtUtc,
        LiveBackupTreeKind treeKind, CancellationToken cancellationToken)
        => inner.BeginGenerationAsync(generationId, startedAtUtc, treeKind, cancellationToken);

    public Task<bool> UseSegmentAsync(long generationId, DiskSegmentFile file, CancellationToken cancellationToken)
        => inner.UseSegmentAsync(generationId, file, cancellationToken);

    public Task UploadSegmentFileAsync(long generationId, DiskSegmentFile file, Stream source,
        CancellationToken cancellationToken)
        => inner.UploadSegmentFileAsync(generationId, file, source, cancellationToken);

    public Task<ILiveBackupRecordWriter> OpenRecordWriterAsync(long generationId, LiveBackupRecordBatch batch,
        CancellationToken cancellationToken)
        => inner.OpenRecordWriterAsync(generationId, batch, cancellationToken);

    public async Task CompleteGenerationAsync(long generationId, long lastOpIndex,
        CancellationToken cancellationToken)
    {
      if (!await publishing.ArriveAsync().WaitAsync(cancellationToken).ConfigureAwait(false))
        throw new InvalidOperationException("Another column of the cluster backup generation failed.");
      await inner.CompleteGenerationAsync(generationId, lastOpIndex, cancellationToken).ConfigureAwait(false);
    }
  }
}
