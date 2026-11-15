namespace ZoneTree.Logger;

public sealed class ConsoleLogger : ILogger
{
  readonly Lock SyncRoot = new();

#pragma warning disable CA2211 // Non-constant fields should not be visible
  public static LogLevel DefaultLogLevel = LogLevel.Warning;
#pragma warning restore CA2211 // Non-constant fields should not be visible

  public LogLevel LogLevel { get; set; }

  public ConsoleLogger()
  {
    LogLevel = DefaultLogLevel;
  }

  public ConsoleLogger(LogLevel logLevel)
  {
    LogLevel = logLevel;
  }

  public void LogError(Exception log)
  {
    if (LogLevel > LogLevel.Error)
      return;
    lock (SyncRoot)
    {
      var existing = Console.ForegroundColor;
      Console.ForegroundColor = ConsoleColor.Red;
      Console.WriteLine(log.ToString());
      Console.ForegroundColor = existing;
    }
  }

  public void LogInfo(object log)
  {
    if (LogLevel > LogLevel.Info)
      return;
    lock (SyncRoot)
    {
      var existing = Console.ForegroundColor;
      Console.ForegroundColor = ConsoleColor.Green;
      Console.WriteLine(log.ToString());
      Console.ForegroundColor = existing;
    }
  }

  public void LogTrace(object log)
  {
    if (LogLevel > LogLevel.Trace)
      return;
    lock (SyncRoot)
    {
      var existing = Console.ForegroundColor;
      Console.ForegroundColor = ConsoleColor.Cyan;
      Console.WriteLine(log.ToString());
      Console.ForegroundColor = existing;
    }
  }

  public void LogWarning(object log)
  {
    if (LogLevel > LogLevel.Warning)
      return;
    lock (SyncRoot)
    {
      var existing = Console.ForegroundColor;
      Console.ForegroundColor = ConsoleColor.Yellow;
      Console.WriteLine(log.ToString());
      Console.ForegroundColor = existing;
    }
  }
}
