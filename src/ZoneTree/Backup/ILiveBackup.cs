namespace ZoneTree.Backup;

/// <summary>Controls live backup generations for a plain or transactional tree.</summary>
public interface ILiveBackup : IDisposable
{
  /// <summary>Latest generation completed successfully by this coordinator; also exposed by GetStatus.</summary>
  long CurrentGenerationId { get; }

  /// <summary>Returns a consistent in-memory status without storage I/O. Available after disposal.</summary>
  LiveBackupStatus GetStatus();
  void Start();
  void Stop();
  void CreateGeneration();
  Task CreateGenerationAsync(CancellationToken cancellationToken = default);
  void WaitForLiveBackup();
  Task WaitForLiveBackupAsync(CancellationToken cancellationToken = default);
}
