namespace ZoneTree.Backup;

/// <summary>
/// Reads live backup generations for restore.
/// </summary>
public interface ILiveBackupSource
{
  /// <summary>Returns a generation that must be disposed after its streams have been read.</summary>
  Task<LiveBackupGeneration> ReadLatestGenerationAsync(
      CancellationToken cancellationToken);

  /// <summary>Returns a generation that must be disposed after its streams have been read.</summary>
  Task<LiveBackupGeneration> ReadGenerationAsync(
      long generationId,
      CancellationToken cancellationToken);

  Task<Stream> OpenSegmentFileAsync(
      LiveBackupFile file,
      CancellationToken cancellationToken);

  Task<Stream> OpenRecordBatchAsync(
      LiveBackupRecordBatch batch,
      CancellationToken cancellationToken);
}
