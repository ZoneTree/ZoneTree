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

  [Test]
  public void SetLogLevelRefusesAnotherLogger()
  {
    var factory = new ZoneTreeFactory<int, int>().SetLogger(SilentTestLogger.Instance);
    var exception = Assert.Throws<InvalidOperationException>(() => factory.SetLogLevel(ZoneTreeLogLevel.Trace));
    Assert.That(exception.Message, Does.Contain("Set the level on that logger instead"));
  }

  sealed class CountingLogger(ZoneTreeLogLevel level) : IZoneTreeLogger
  {
    public int Count;

    public bool IsEnabled(ZoneTreeLogLevel entryLevel) => entryLevel >= level;

    public void Log(ZoneTreeLogLevel entryLevel, string message, Exception exception = null) => ++Count;
  }

  [Test]
  public void TheShortCallsSkipEntriesTheLoggerDoesNotWrite()
  {
    var logger = new CountingLogger(ZoneTreeLogLevel.Error);
    logger.LogTrace("trace");
    logger.LogInfo("info");
    logger.LogWarning("warning");
    Assert.That(logger.Count, Is.Zero);
    logger.LogError("error");
    Assert.That(logger.Count, Is.EqualTo(1));
  }
}
