namespace ZoneTree.Backup;

/// <summary>Controls live backup generations for a plain or transactional tree.</summary>
public interface ILiveBackup : IDisposable
{
  long CurrentGenerationId { get; }
  void Start();
  void Stop();
  void CreateGeneration();
  Task CreateGenerationAsync(CancellationToken cancellationToken = default);
  void WaitForLiveBackup();
  Task WaitForLiveBackupAsync(CancellationToken cancellationToken = default);
}
