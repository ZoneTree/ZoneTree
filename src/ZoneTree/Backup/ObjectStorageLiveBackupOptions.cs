namespace ZoneTree.Backup;

public sealed class ObjectStorageLiveBackupOptions
{
  /// <summary>A namespace dedicated to one source database. Separate databases must use separate prefixes.</summary>
  public string Prefix { get; set; } = "zonetree-backup";

  /// <summary>Number of published generations to retain, plus pinned restores. Null retains all published generations.</summary>
  public int? KeepLastGenerations { get; set; }

  /// <summary>Maximum manifest or catalog size, including integrity framing. Oversized metadata fails closed.</summary>
  public int MaximumMetadataBytes { get; set; } = 16 * 1024 * 1024;
}

/// <summary>A persistent restore pin. Release an abandoned pin only after its consumer has stopped.</summary>
public sealed class ObjectStorageBackupRestorePin
{
  public string Id { get; }
  public long GenerationId { get; }
  public long CreatedAtUnixSeconds { get; }

  [System.Text.Json.Serialization.JsonConstructor]
  public ObjectStorageBackupRestorePin(string id, long generationId, long createdAtUnixSeconds)
  { Id = id; GenerationId = generationId; CreatedAtUnixSeconds = createdAtUnixSeconds; }
}
