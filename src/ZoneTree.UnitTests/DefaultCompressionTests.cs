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
}
