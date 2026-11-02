using ZoneTree.Logger;

namespace ZoneTree.UnitTests;

/// <summary>
/// Keeps expected fault-injection exceptions out of successful test output.
/// NUnit assertions and the simulator's failing-image diagnostics still print.
/// </summary>
internal sealed class SilentTestLogger : ILogger
{
  public static readonly SilentTestLogger Instance = new();

  public LogLevel LogLevel { get; set; } = LogLevel.Error;

  public void LogError(Exception log) { }

  public void LogWarning(object log) { }

  public void LogInfo(object log) { }

  public void LogTrace(object log) { }
}
