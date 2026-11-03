namespace ZoneTree.Backup;

/// <summary>
/// Options for the local-directory live backup provider.
/// </summary>
public sealed class LocalLiveBackupOptions
{
  /// <summary>
  /// The local backup root directory.
  /// </summary>
  public string Directory { get; set; }

  /// <summary>
  /// Buffer size used while copying backup files.
  /// </summary>
  public int CopyBufferSize { get; set; } = 128 * 1024;

  /// <summary>
  /// Keeps the latest published backup generations, plus any held by active restores.
  /// Released generations are reclaimed after a later successful backup.
  /// Null disables local retention.
  /// </summary>
  public int? KeepLastGenerations { get; set; }

  public void Normalize()
  {
    if (CopyBufferSize <= 0)
      CopyBufferSize = 128 * 1024;
    if (KeepLastGenerations.HasValue &&
        KeepLastGenerations.Value <= 0)
    {
      KeepLastGenerations = null;
    }
  }
}
