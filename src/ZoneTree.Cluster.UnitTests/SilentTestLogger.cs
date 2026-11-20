using ZoneTree.Logging;

namespace ZoneTree.Cluster.UnitTests;

/// <summary>
/// Keeps expected fault-injection exceptions out of successful test output.
/// NUnit assertions and the simulator's failing-image diagnostics still print.
/// </summary>
internal sealed class SilentTestLogger : IZoneTreeLogger
{
  public ZoneTreeLogLevel Level { get; set; }

  public static readonly SilentTestLogger Instance = new();

  public bool IsEnabled(ZoneTreeLogLevel level) => false;

  public void Log(ZoneTreeLogLevel level, string message, Exception exception = null) { }
}
