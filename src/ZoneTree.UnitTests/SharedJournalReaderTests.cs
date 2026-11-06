using System.Buffers.Binary;
using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class SharedJournalReaderTests
{
  const string Directory = "data";

  static ZoneTreeFactory<int, int> Factory(IFileStreamProvider provider)
      => new ZoneTreeFactory<int, int>(provider)
          .SetDataDirectory(Directory)
          .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.Shared);

  static int[] ReadKeys(IFileStreamProvider provider)
      => SharedJournalReader.ReadLiveRecords(new ConsoleLogger(LogLevel.Error), provider, Directory, new WriteAheadLogOptions())
          .Select(record => BinaryPrimitives.ReadInt32LittleEndian(record.Key.Span))
          .ToArray();

  [Test]
  public void ReadsTheLiveSegmentsOfAClosedTreeInWriteOrder()
  {
    var provider = new InMemoryFileStreamProvider();
    Assert.That(ReadKeys(provider), Is.Empty);

    using (var tree = Factory(provider).OpenOrCreate())
    {
      tree.Upsert(1, 10);
      tree.Upsert(2, 20);
      tree.Upsert(1, 11);
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Upsert(3, 30);
    }
    // The read-only segment from the oldest, then the mutable one, every write in order.
    Assert.That(ReadKeys(provider), Is.EqualTo(new[] { 1, 2, 1, 3 }));

    using (var tree = Factory(provider).Open())
    {
      Assert.That(tree.TryGet(1, out var value) && value == 11, Is.True);
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Maintenance.StartMergeOperation().Join();
      tree.Upsert(4, 40);
    }
    // Merged segments leave the journal; their records are in the disk segment.
    Assert.That(ReadKeys(provider), Is.EqualTo(new[] { 4 }));

    // Reading changed nothing the tree depends on.
    using (var tree = Factory(provider).Open())
    {
      foreach (var (key, expected) in new[] { (1, 11), (2, 20), (3, 30), (4, 40) })
        Assert.That(tree.TryGet(key, out var value) && value == expected, Is.True, $"key {key}");
    }
  }
}
