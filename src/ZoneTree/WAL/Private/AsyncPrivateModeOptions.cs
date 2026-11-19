using ZoneTree.Options;

namespace ZoneTree.WAL;

public sealed class AsyncPrivateModeOptions
{
  /// <summary>
  /// The delay in milliseconds before making the next poll
  /// to retrieve the new entries in the queue,
  /// when the queue is empty.
  /// </summary>
  public int EmptyQueuePollInterval { get; set; } = WriteAheadLogDefaultValues.AsyncPrivateModeEmptyQueuePollInterval;
}
