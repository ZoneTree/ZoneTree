using ZoneTree.AbstractFileStream;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.UnitTests;

/// <summary>
/// Tests that a merge reads a disk segment record's value only when the record is kept. A record that a newer
/// segment replaces is skipped by its key, without deserializing its value.
/// </summary>
public sealed class MergeValueReadTests
{
  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void AMergeDoesNotDeserializeValuesThatNewerSegmentsReplace(DiskSegmentMode mode)
  {
    const int count = 1000;
    var values = new RecordingSerializer();
    using var tree = new ZoneTreeFactory<int, string>(new InMemoryFileStreamProvider())
        .SetDataDirectory("data/MergeValueReads" + mode)
        .SetValueSerializer(values)
        .DisableDeletion()
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DiskSegmentMode = mode;
          options.ValueCacheSize = 0;
        })
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.None)
        .OpenOrCreate();
    for (var key = 0; key < count; ++key)
      tree.Upsert(key, "old-" + key);
    tree.Maintenance.MoveMutableSegmentForward();
    tree.Maintenance.StartMergeOperation().Join();
    // A newer segment replaces every even key.
    for (var key = 0; key < count; key += 2)
      tree.Upsert(key, "new-" + key);
    tree.Maintenance.MoveMutableSegmentForward();

    values.Deserialized.Clear();
    tree.Maintenance.StartMergeOperation().Join();

    var replacedReads = values.Deserialized
        .Where(value => value.StartsWith("old-", StringComparison.Ordinal) && int.Parse(value[4..]) % 2 == 0);
    Assert.That(replacedReads, Is.Empty);
    for (var key = 0; key < count; ++key)
    {
      Assert.That(tree.TryGet(key, out var value), Is.True);
      Assert.That(value, Is.EqualTo((key % 2 == 0 ? "new-" : "old-") + key));
    }
  }

  sealed class RecordingSerializer : ISerializer<string>
  {
    readonly Utf8StringSerializer Inner = new();

    public readonly List<string> Deserialized = new();

    public string Deserialize(Memory<byte> bytes)
    {
      var value = Inner.Deserialize(bytes);
      lock (Deserialized)
        Deserialized.Add(value);
      return value;
    }

    public Memory<byte> Serialize(in string entry) => Inner.Serialize(entry);
  }
}
