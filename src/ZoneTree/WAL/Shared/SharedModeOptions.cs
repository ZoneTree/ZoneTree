namespace ZoneTree.WAL;

/// <summary>Physical chunk sizing for a shared, ordered write ahead log.</summary>
public sealed class SharedModeOptions
{
  /// <summary>
  /// The target uncompressed size of a journal chunk, in bytes. A record is
  /// never split between chunks; a larger record gets a larger chunk.
  /// Old chunks are removed after their segment records are checkpointed.
  /// Default is 64 MiB.
  /// </summary>
  public long MaximumChunkSize { get; set; } = 64L * 1024 * 1024;
}
