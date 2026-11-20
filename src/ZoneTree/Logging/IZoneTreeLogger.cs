namespace ZoneTree.Logging;

/// <summary>
/// Receives the log entries of ZoneTree: errors and warnings of the work it does in the
/// background, such as merges, WAL syncs, recovery and backups, and the steps of that work
/// at the trace level.
/// </summary>
/// <remarks>
/// <para>
/// ZoneTree calls the logger from background threads, so an implementation must be
/// thread-safe.
/// </para>
/// <para>
/// An implementation keeps <see cref="Level"/>, answers <see cref="IsEnabled"/> from it, and
/// writes the entries in <see cref="Log"/>. An implementation that forwards the entries to
/// another logging library can also ask that library in <see cref="IsEnabled"/>.
/// <see cref="ZoneTreeLoggerExtensions"/> adds short calls for each level.
/// </para>
/// </remarks>
public interface IZoneTreeLogger
{
  /// <summary>
  /// The least severe level the logger writes.
  /// <see cref="ZoneTreeFactory{TKey, TValue}.SetLogLevel(ZoneTreeLogLevel)"/> sets it.
  /// </summary>
  ZoneTreeLogLevel Level { get; set; }

  /// <summary>
  /// Returns whether the logger writes entries of the level: usually whether the level is
  /// <see cref="Level"/> or more severe, and not <see cref="ZoneTreeLogLevel.None"/>. ZoneTree
  /// asks before it builds a message that costs something to build.
  /// </summary>
  /// <param name="level">The level of an entry.</param>
  /// <returns>true if the logger writes entries of the level; otherwise, false.</returns>
  bool IsEnabled(ZoneTreeLogLevel level);

  /// <summary>
  /// Writes an entry.
  /// </summary>
  /// <param name="level">The level of the entry. It is never <see cref="ZoneTreeLogLevel.None"/>.</param>
  /// <param name="message">What happened, and what happens next.</param>
  /// <param name="exception">The exception that caused the entry, or null.</param>
  void Log(ZoneTreeLogLevel level, string message, Exception exception = null);
}
