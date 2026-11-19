using ZoneTree.Backup;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

public sealed class DefaultCompressionTests
{
  [Test]
  public void DiskSegmentCompressionDefaultsToZstd()
  {
    var options = new DiskSegmentOptions();

    Assert.That(options.CompressionMethod, Is.EqualTo(CompressionMethod.Zstd));
    Assert.That(options.CompressionLevel, Is.EqualTo(CompressionLevels.Zstd1));
  }

  [Test]
  public void WriteAheadLogCompressionDefaultsToZstd()
  {
    var options = new WriteAheadLogOptions();

    Assert.That(options.CompressionMethod, Is.EqualTo(CompressionMethod.Zstd));
    Assert.That(options.CompressionLevel, Is.EqualTo(CompressionLevels.Zstd1));
  }

  [Test]
  public void LiveBackupRecordBatchCompressionDefaultsToZstd()
  {
    var options = new LiveBackupCompressionOptions();

    Assert.That(options.Method, Is.EqualTo(CompressionMethod.Zstd));
    Assert.That(options.Level, Is.EqualTo(CompressionLevels.Zstd1));
  }

  [Test]
  public void LiveBackupWithLZ4AndTheDefaultLevelCompressesWithLZ4Fastest()
  {
    var options = new LiveBackupCompressionOptions { Method = CompressionMethod.LZ4 };

    options.Normalize();

    Assert.That(options.Method, Is.EqualTo(CompressionMethod.LZ4));
    Assert.That(options.Level, Is.EqualTo(CompressionLevels.LZ4Fastest));
  }

  [Test]
  public void LZ4WithTheDefaultLevelCompressesWithLZ4Fastest()
  {
    var factory = new ZoneTreeFactory<int, int>()
        .ConfigureWriteAheadLogOptions(x => x.CompressionMethod = CompressionMethod.LZ4)
        .ConfigureDiskSegmentOptions(x => x.CompressionMethod = CompressionMethod.LZ4)
        .Validate();

    Assert.That(factory.Options.WriteAheadLogOptions.CompressionLevel, Is.EqualTo(CompressionLevels.LZ4Fastest));
    Assert.That(factory.Options.DiskSegmentOptions.CompressionLevel, Is.EqualTo(CompressionLevels.LZ4Fastest));
  }

  [Test]
  public void LZ4KeepsAChosenLevel()
  {
    var factory = new ZoneTreeFactory<int, int>()
        .ConfigureWriteAheadLogOptions(x =>
        {
          x.CompressionMethod = CompressionMethod.LZ4;
          x.CompressionLevel = CompressionLevels.LZ4HighCompression9;
        })
        .ConfigureDiskSegmentOptions(x =>
        {
          x.CompressionMethod = CompressionMethod.LZ4;
          x.CompressionLevel = CompressionLevels.LZ4HighCompression9;
        })
        .Validate();

    Assert.That(factory.Options.WriteAheadLogOptions.CompressionLevel, Is.EqualTo(CompressionLevels.LZ4HighCompression9));
    Assert.That(factory.Options.DiskSegmentOptions.CompressionLevel, Is.EqualTo(CompressionLevels.LZ4HighCompression9));
  }
}
