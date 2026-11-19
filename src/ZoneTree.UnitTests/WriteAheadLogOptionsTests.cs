using ZoneTree.Options;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class WriteAheadLogOptionsTests
{
  [Test]
  public void CloneCopiesEverySetting()
  {
    var customOptions = new object();
    var options = new WriteAheadLogOptions
    {
      WriteAheadLogMode = WriteAheadLogMode.Shared,
      CustomOptions = customOptions,
      CompressionBlockSize = 4096,
      CompressionMethod = CompressionMethod.None,
      CompressionLevel = 0,
      EnableSyncJob = false,
      SyncJobInterval = 17,
      EmptyQueuePollInterval = 19,
      SharedModeOptions = new SharedModeOptions { MaximumChunkSize = 1024 }
    };

    var clone = options.Clone();

    Assert.That(clone, Is.Not.SameAs(options));
    Assert.That(clone.WriteAheadLogMode, Is.EqualTo(WriteAheadLogMode.Shared));
    Assert.That(clone.CustomOptions, Is.SameAs(customOptions));
    Assert.That(clone.CompressionBlockSize, Is.EqualTo(4096));
    Assert.That(clone.CompressionMethod, Is.EqualTo(CompressionMethod.None));
    Assert.That(clone.CompressionLevel, Is.Zero);
    Assert.That(clone.EnableSyncJob, Is.False);
    Assert.That(clone.SyncJobInterval, Is.EqualTo(17));
    Assert.That(clone.EmptyQueuePollInterval, Is.EqualTo(19));
    Assert.That(clone.SharedModeOptions, Is.Not.SameAs(options.SharedModeOptions));
    Assert.That(clone.SharedModeOptions.MaximumChunkSize, Is.EqualTo(1024));
  }
}
