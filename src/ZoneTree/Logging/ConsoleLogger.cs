namespace ZoneTree.Logging;

/// <summary>
/// Writes the log entries of ZoneTree to the standard error stream, one line per entry,
/// with the time in UTC and the level. It is the default logger of a tree.
/// </summary>
/// <remarks>
/// The standard error stream keeps the standard output free for the application, such as a
/// tool whose output is piped. An entry with an exception continues with the exception on
/// the following lines.
/// </remarks>
public sealed class ConsoleLogger : IZoneTreeLogger
{
  /// <summary>
  /// Creates a console logger that writes entries of the level and the more severe ones.
  /// </summary>
  /// <param name="level">The least severe level to write. The default writes warnings and errors.</param>
  public ConsoleLogger(ZoneTreeLogLevel level = ZoneTreeLogLevel.Warning)
  {
    Level = level;
  }

  /// <summary>
  /// The least severe level the logger writes.
  /// </summary>
  public ZoneTreeLogLevel Level { get; set; }

  /// <inheritdoc/>
  public bool IsEnabled(ZoneTreeLogLevel level) => level != ZoneTreeLogLevel.None && level >= Level;

  /// <inheritdoc/>
  public void Log(ZoneTreeLogLevel level, string message, Exception exception = null)
  {
    if (!IsEnabled(level))
      return;
    var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} ZoneTree {GetName(level)}: {message}";
    if (exception != null)
      line += Environment.NewLine + exception;
    // One write per entry, so the entries of concurrent threads do not interleave.
    Console.Error.WriteLine(line);
  }

  static string GetName(ZoneTreeLogLevel level) => level switch
  {
    ZoneTreeLogLevel.Trace => "trace",
    ZoneTreeLogLevel.Info => "info",
    ZoneTreeLogLevel.Warning => "warning",
    _ => "error"
  };
}
