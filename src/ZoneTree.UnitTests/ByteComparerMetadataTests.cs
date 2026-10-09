using System.Text;
using System.Text.Json;
using ZoneTree.AbstractFileStream;
using ZoneTree.Comparers;
using ZoneTree.Core;
using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Segments.RandomAccess;

namespace ZoneTree.UnitTests;

public sealed class ByteComparerMetadataTests
{
  [TestCase(false, false)]
  [TestCase(false, true)]
  [TestCase(true, false)]
  [TestCase(true, true)]
  public void OldComparerMetadataFailsBeforeLoadingSegments(bool descending, bool openOrCreate)
  {
    var provider = new InMemoryFileStreamProvider();
    Memory<byte> prefix = new byte[] { 1 };
    Memory<byte> longer = new byte[] { 1, 2 };

    ZoneTreeFactory<Memory<byte>, int> CreateFactory()
    {
      var factory = new ZoneTreeFactory<Memory<byte>, int>(provider)
          .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.Sync);
      if (descending)
        factory.SetComparer(new ByteArrayLexicographicComparerDescending());
      return factory;
    }

    using (var tree = CreateFactory().Create())
    {
      tree.Upsert(prefix, 1);
      tree.Upsert(longer, 2);
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Maintenance.StartMergeOperation().Join();
      tree.Maintenance.SaveMetaData();
    }

    var metadataPath = provider.CombinePaths("data", "0.json");
    var originalJson = provider.ReadAllText(metadataPath);
    var metadata = JsonSerializer.Deserialize<ZoneTreeMeta>(originalJson);
    var newComparerType = descending
        ? typeof(ByteArrayLexicographicComparerDescending).SimplifiedFullName()
        : typeof(ByteArrayLexicographicComparerAscending).SimplifiedFullName();
    Assert.That(metadata.ComparerType, Is.EqualTo(newComparerType));

    // Simulate the comparer identity persisted by a previous ZoneTree version.
    var oldComparerType = descending
        ? "ZoneTree.Comparers.ByteArrayComparerDescending"
        : "ZoneTree.Comparers.ByteArrayComparerAscending";
    metadata.ComparerType = oldComparerType;
    WriteMetadata(provider, metadataPath, JsonSerializer.Serialize(metadata));
    var oldMetadataBytes = provider.ReadAllBytes(metadataPath);

    var factory = CreateFactory();
    var exception = Assert.Throws<TreeComparerMismatchException>(() =>
    {
      using var tree = openOrCreate ? factory.OpenOrCreate() : factory.Open();
    });

    Assert.That(exception.ExpectedComparerType, Is.EqualTo(oldComparerType));
    Assert.That(exception.GivenComparerType, Is.EqualTo(newComparerType));
    Assert.That(exception.Message, Does.Contain("rebuild the database"));
    Assert.That(provider.ReadAllBytes(metadataPath), Is.EqualTo(oldMetadataBytes));
    var deviceManager = (RandomAccessDeviceManager)factory.Options.RandomAccessDeviceManager;
    Assert.That(deviceManager.DeviceCount, Is.Zero);

    // Restore the test's original metadata and verify the rejected open preserved data.
    WriteMetadata(provider, metadataPath, originalJson);
    using var reopened = CreateFactory().Open();
    Assert.That(reopened.TryGet(prefix, out var prefixValue), Is.True);
    Assert.That(prefixValue, Is.EqualTo(1));
    Assert.That(reopened.TryGet(longer, out var longerValue), Is.True);
    Assert.That(longerValue, Is.EqualTo(2));
  }

  static void WriteMetadata(InMemoryFileStreamProvider provider, string path, string json)
  {
    using var stream = provider.CreateFileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    var bytes = Encoding.UTF8.GetBytes(json);
    stream.Write(bytes, 0, bytes.Length);
  }
}
