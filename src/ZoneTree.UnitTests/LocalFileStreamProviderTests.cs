using ZoneTree.AbstractFileStream;

namespace ZoneTree.UnitTests;

public sealed class LocalFileStreamProviderTests
{
  [Test]
  public void ReplaceRetriesTransientIoExceptionAndEventuallySucceeds()
  {
    var attemptCount = 0;
    var delays = new List<int>();
    var provider = new LocalFileStreamProvider(
        (_, _, _) =>
        {
          ++attemptCount;
          if (attemptCount < 3)
            throw new IOException("Unable to remove the file to be replaced.");
        },
        delays.Add);

    provider.Replace("source", "destination", null);

    Assert.Multiple(() =>
    {
      Assert.That(attemptCount, Is.EqualTo(3));
      Assert.That(delays, Is.EqualTo([10, 25]));
    });
  }

  [Test]
  public void ReplaceRethrowsAfterBoundedRetryAttempts()
  {
    var attemptCount = 0;
    var delays = new List<int>();
    var provider = new LocalFileStreamProvider(
        (_, _, _) =>
        {
          ++attemptCount;
          throw new IOException("Unable to remove the file to be replaced.");
        },
        delays.Add);

    Assert.Throws<IOException>(() => provider.Replace("source", "destination", null));

    Assert.Multiple(() =>
    {
      Assert.That(attemptCount, Is.EqualTo(6));
      Assert.That(delays, Is.EqualTo([10, 25, 50, 100, 200]));
    });
  }

  [Test]
  public void ReplaceDoesNotRetryMissingSourceFile()
  {
    var attemptCount = 0;
    var delays = new List<int>();
    var provider = new LocalFileStreamProvider(
        (_, _, _) =>
        {
          ++attemptCount;
          throw new FileNotFoundException("source");
        },
        delays.Add);

    Assert.Throws<FileNotFoundException>(() => provider.Replace("source", "destination", null));

    Assert.Multiple(() =>
    {
      Assert.That(attemptCount, Is.EqualTo(1));
      Assert.That(delays, Is.Empty);
    });
  }
}
