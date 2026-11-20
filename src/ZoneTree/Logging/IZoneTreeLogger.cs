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
/// To send the entries to another logging library, implement the two members by forwarding
/// them. <see cref="ZoneTreeLoggerExtensions"/> adds short calls for each level.
/// </para>
/// </remarks>
public interface IZoneTreeLogger
{
  /// <summary>
  /// Returns whether the logger writes entries of the level. ZoneTree asks before it builds
  /// a message that costs something to build.
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
