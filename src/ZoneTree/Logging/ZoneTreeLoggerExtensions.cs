namespace ZoneTree.Logging;

/// <summary>
/// Short calls for each level of <see cref="IZoneTreeLogger"/>. Each writes the entry only if
/// the logger writes entries of its level.
/// </summary>
public static class ZoneTreeLoggerExtensions
{
  /// <summary>
  /// Writes an error.
  /// </summary>
  /// <param name="logger">The logger.</param>
  /// <param name="message">What failed, and what happens next.</param>
  /// <param name="exception">The exception that caused the error, or null.</param>
  public static void LogError(this IZoneTreeLogger logger, string message, Exception exception = null)
    => Write(logger, ZoneTreeLogLevel.Error, message, exception);

  /// <summary>
  /// Writes a warning.
  /// </summary>
  /// <param name="logger">The logger.</param>
  /// <param name="message">What happened, and what happens next.</param>
  /// <param name="exception">The exception that caused the warning, or null.</param>
  public static void LogWarning(this IZoneTreeLogger logger, string message, Exception exception = null)
    => Write(logger, ZoneTreeLogLevel.Warning, message, exception);

  /// <summary>
  /// Writes information.
  /// </summary>
  /// <param name="logger">The logger.</param>
  /// <param name="message">What happened.</param>
  public static void LogInfo(this IZoneTreeLogger logger, string message)
    => Write(logger, ZoneTreeLogLevel.Info, message, null);

  /// <summary>
  /// Writes a step of the work ZoneTree does.
  /// </summary>
  /// <param name="logger">The logger.</param>
  /// <param name="message">What happened.</param>
  public static void LogTrace(this IZoneTreeLogger logger, string message)
    => Write(logger, ZoneTreeLogLevel.Trace, message, null);

  static void Write(IZoneTreeLogger logger, ZoneTreeLogLevel level, string message, Exception exception)
  {
    if (logger.IsEnabled(level))
      logger.Log(level, message, exception);
  }
}
