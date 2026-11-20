using ZoneTree.Logging;

namespace ZoneTree.UnitTests;

[NonParallelizable]
public sealed class ZoneTreeLoggerTests
{
  [Test]
  public void TheConsoleLoggerWritesEntriesOfItsLevelToStandardError()
  {
    var output = new StringWriter();
    var error = new StringWriter();
    var originalOutput = Console.Out;
    var originalError = Console.Error;
    Console.SetOut(output);
    Console.SetError(error);
    try
    {
      var logger = new ConsoleLogger(ZoneTreeLogLevel.Warning);
      logger.LogInfo("An info entry.");
      logger.LogWarning("A warning entry.");
      logger.LogError("An error entry.", new InvalidOperationException("The cause."));
    }
    finally
    {
      Console.SetOut(originalOutput);
      Console.SetError(originalError);
    }
    var written = error.ToString();
    Assert.That(output.ToString(), Is.Empty);
    Assert.That(written, Does.Not.Contain("An info entry."));
    Assert.That(written, Does.Contain("ZoneTree warning: A warning entry."));
    Assert.That(written, Does.Contain("ZoneTree error: An error entry."));
    Assert.That(written, Does.Contain("System.InvalidOperationException: The cause."));
  }

  [Test]
  public void TheConsoleLoggerWritesNothingAtLevelNone()
  {
    var logger = new ConsoleLogger(ZoneTreeLogLevel.None);
    Assert.That(logger.IsEnabled(ZoneTreeLogLevel.Error), Is.False);
    Assert.That(logger.IsEnabled(ZoneTreeLogLevel.None), Is.False);
  }

  [Test]
  public void SetLogLevelSetsTheLevelOfTheConsoleLogger()
  {
    var factory = new ZoneTreeFactory<int, int>().SetLogLevel(ZoneTreeLogLevel.Trace);
    Assert.That(((ConsoleLogger)factory.Options.Logger).Level, Is.EqualTo(ZoneTreeLogLevel.Trace));
  }

  sealed class CountingLogger(ZoneTreeLogLevel level) : IZoneTreeLogger
  {
    public int Count;

    public ZoneTreeLogLevel Level { get; set; } = level;

    public bool IsEnabled(ZoneTreeLogLevel entryLevel) => entryLevel != ZoneTreeLogLevel.None && entryLevel >= Level;

    public void Log(ZoneTreeLogLevel entryLevel, string message, Exception exception = null) => ++Count;
  }

  [Test]
  public void SetLogLevelSetsTheLevelOfAnyLogger()
  {
    var logger = new CountingLogger(ZoneTreeLogLevel.Error);
    new ZoneTreeFactory<int, int>().SetLogger(logger).SetLogLevel(ZoneTreeLogLevel.Info);
    Assert.That(logger.Level, Is.EqualTo(ZoneTreeLogLevel.Info));
  }

  [Test]
  public void TheShortCallsSkipEntriesBelowTheLevel()
  {
    var logger = new CountingLogger(ZoneTreeLogLevel.Error);
    logger.LogTrace("trace");
    logger.LogInfo("info");
    logger.LogWarning("warning");
    Assert.That(logger.Count, Is.Zero);
    logger.LogError("error");
    Assert.That(logger.Count, Is.EqualTo(1));
    logger.Level = ZoneTreeLogLevel.None;
    logger.LogError("error");
    Assert.That(logger.Count, Is.EqualTo(1));
  }
}
