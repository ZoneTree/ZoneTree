using ZoneTree.AbstractFileStream;
using ZoneTree.Comparers;
using ZoneTree.Options;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class ByteMemoryTreeTests
{
  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void MemoryValueDeletionSurvivesSegmentChangesAndReload(DiskSegmentMode mode)
  {
    var provider = new InMemoryFileStreamProvider();
    var factory = CreateFactory<int, Memory<byte>>(provider, mode);

    using (var tree = factory.Create())
    {
      for (var key = 1; key <= 4; ++key)
        tree.Upsert(key, new byte[] { (byte)key }.AsMemory());
      Merge(tree);
      Assert.That(tree.Maintenance.DiskSegment.Length, Is.EqualTo(4));

      tree.Upsert(5, new byte[] { 5 }.AsMemory());
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Upsert(6, new byte[] { 6 }.AsMemory());

      // Delete entries in disk, read-only, and mutable segments respectively.
      tree.ForceDelete(1);
      Assert.That(tree.TryDelete(5, out _), Is.True);
      tree.ForceDelete(6);
      tree.ForceDelete(9);
      AssertMemoryDeletions(tree);
      Assert.That(tree.TryDelete(5, out _), Is.False);

      tree.Maintenance.MoveMutableSegmentForward();
      AssertMemoryDeletions(tree);
      // Leave tombstones in the WAL for the first reopen.
    }

    using (var tree = CreateFactory<int, Memory<byte>>(provider, mode).Open())
    {
      AssertMemoryDeletions(tree);
      Merge(tree);
      AssertMemoryDeletions(tree);
    }

    using (var tree = CreateFactory<int, Memory<byte>>(provider, mode).Open())
    {
      AssertMemoryDeletions(tree);
      tree.Upsert(1, new byte[] { 42 }.AsMemory());
      Assert.That(tree.TryGet(1, out var restored), Is.True);
      Assert.That(restored.ToArray(), Is.EqualTo(new byte[] { 42 }));
    }
  }

  [TestCase(false, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(false, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(true, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(true, DiskSegmentMode.MultiPartDiskSegment)]
  public void ByteKeysSortAndSeekAcrossSegmentsAndReload(bool descending, DiskSegmentMode mode)
  {
    var provider = new InMemoryFileStreamProvider();
    Memory<byte>[] ordered =
    [
      Memory<byte>.Empty,
      new byte[] { 0 },
      new byte[] { 1 },
      new byte[] { 1, 0, 1 },
      new byte[] { 1, 0, 2 },
      new byte[] { 1, 1 },
      new byte[] { 2 }
    ];

    ZoneTreeFactory<Memory<byte>, int> CreateKeyFactory()
    {
      var factory = CreateFactory<Memory<byte>, int>(provider, mode);
      if (descending)
        factory.SetComparer(new ByteArrayLexicographicComparerDescending());
      return factory;
    }

    using (var tree = CreateKeyFactory().Create())
    {
      // Insert out of order to exercise tree placement as well as iteration.
      for (var i = ordered.Length - 1; i >= 0; --i)
        tree.Upsert(ordered[i], i + 1);
      AssertByteOrderAndSeeks(tree, ordered, descending);

      tree.Maintenance.MoveMutableSegmentForward();
      AssertByteOrderAndSeeks(tree, ordered, descending);
      tree.Maintenance.StartMergeOperation().Join();
      Assert.That(tree.Maintenance.DiskSegment.Length, Is.EqualTo(ordered.Length));
      AssertByteOrderAndSeeks(tree, ordered, descending);
    }

    using (var tree = CreateKeyFactory().Open())
      AssertByteOrderAndSeeks(tree, ordered, descending);
  }

  static void AssertMemoryDeletions(IZoneTree<int, Memory<byte>> tree)
  {
    foreach (var key in new[] { 1, 5, 6, 9 })
    {
      Assert.That(tree.TryGet(key, out _), Is.False, $"Deleted key {key}");
      Assert.That(tree.ContainsKey(key), Is.False, $"Deleted key {key}");
    }

    for (var key = 2; key <= 4; ++key)
    {
      Assert.That(tree.TryGet(key, out var value), Is.True);
      Assert.That(value.ToArray(), Is.EqualTo(new byte[] { (byte)key }));
    }

    using var iterator = tree.CreateIterator();
    var keys = new List<int>();
    while (iterator.Next())
      keys.Add(iterator.CurrentKey);
    Assert.That(keys, Is.EqualTo(new[] { 2, 3, 4 }));
  }

  static void AssertByteOrderAndSeeks(
      IZoneTree<Memory<byte>, int> tree,
      Memory<byte>[] ordered,
      bool descending)
  {
    var expected = (descending ? ordered.Reverse() : ordered).ToArray();
    using (var iterator = tree.CreateIterator())
    {
      foreach (var key in expected)
      {
        Assert.That(iterator.Next(), Is.True);
        Assert.That(iterator.CurrentKey.ToArray(), Is.EqualTo(key.ToArray()));
      }
      Assert.That(iterator.Next(), Is.False);
    }

    using (var iterator = tree.CreateReverseIterator())
    {
      foreach (var key in expected.Reverse())
      {
        Assert.That(iterator.Next(), Is.True);
        Assert.That(iterator.CurrentKey.ToArray(), Is.EqualTo(key.ToArray()));
      }
      Assert.That(iterator.Next(), Is.False);
    }

    // Exact prefix and absent prefix exercise both inclusive and lower-bound seeks.
    Memory<byte> exactPrefix = new byte[] { 1 };
    using (var iterator = tree.CreateIterator())
    {
      iterator.Seek(exactPrefix);
      foreach (var key in expected.SkipWhile(key => !key.Span.SequenceEqual(exactPrefix.Span)))
      {
        Assert.That(iterator.Next(), Is.True);
        Assert.That(iterator.CurrentKey.ToArray(), Is.EqualTo(key.ToArray()));
      }
      Assert.That(iterator.Next(), Is.False);
    }

    Memory<byte> absentPrefix = new byte[] { 1, 0 };
    using (var iterator = tree.CreateIterator())
    {
      iterator.Seek(absentPrefix);
      Assert.That(iterator.Next(), Is.True);
      Assert.That(iterator.CurrentKey.ToArray(),
          Is.EqualTo(descending ? new byte[] { 1 } : new byte[] { 1, 0, 1 }));
    }

    using (var iterator = tree.CreateReverseIterator())
    {
      iterator.Seek(absentPrefix);
      Assert.That(iterator.Next(), Is.True);
      Assert.That(iterator.CurrentKey.ToArray(),
          Is.EqualTo(descending ? new byte[] { 1, 0, 1 } : new byte[] { 1 }));
    }

    foreach (var key in ordered)
      Assert.That(tree.TryGet(key.ToArray().AsMemory(), out _), Is.True);
  }

  static ZoneTreeFactory<TKey, TValue> CreateFactory<TKey, TValue>(
      InMemoryFileStreamProvider provider,
      DiskSegmentMode mode)
  {
    return new ZoneTreeFactory<TKey, TValue>(provider)
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.Sync)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DiskSegmentMode = mode;
          options.MinimumRecordCount = 2;
          options.MaximumRecordCount = 3;
        });
  }

  static void Merge<TKey, TValue>(IZoneTree<TKey, TValue> tree)
  {
    tree.Maintenance.MoveMutableSegmentForward();
    tree.Maintenance.StartMergeOperation().Join();
  }
}
