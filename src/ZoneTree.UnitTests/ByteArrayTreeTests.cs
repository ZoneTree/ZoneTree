using ZoneTree.AbstractFileStream;
using ZoneTree.Comparers;
using ZoneTree.Hashers;
using ZoneTree.Options;
using ZoneTree.PresetTypes;
using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

/// <summary>Byte arrays as built-in key and value types.</summary>
public sealed class ByteArrayTreeTests
{
  [Test]
  public void ByteArraysHaveBuiltInComponents()
  {
    Assert.That(ComponentsForKnownTypes.GetComparer<byte[]>(), Is.TypeOf<BytesLexicographicComparerAscending>());
    Assert.That(ComponentsForKnownTypes.GetKeyHasher<byte[]>(), Is.TypeOf<BytesKeyHasher>());
    Assert.That(ComponentsForKnownTypes.GetSerializer<byte[]>(), Is.TypeOf<BytesSerializer>());

    // No bytes is a deletion, as for byte memory: a null array reads back as an empty one.
    var isDeleted = ComponentsForKnownTypes.GetIsDeleted<int, byte[]>();
    Assert.That(isDeleted(1, null), Is.True);
    Assert.That(isDeleted(1, []), Is.True);
    Assert.That(isDeleted(1, [0]), Is.False);
    byte[] marked = [1];
    ComponentsForKnownTypes.GetMarkValueDeleted<byte[]>()(ref marked);
    Assert.That(isDeleted(1, marked), Is.True);

    var serializer = new BytesSerializer();
    byte[] value = [1, 2, 3];
    Assert.That(serializer.Deserialize(serializer.Serialize(value)), Is.EqualTo(value));
    Assert.That(serializer.Deserialize(serializer.Serialize(null)), Is.Empty);
  }

  [Test]
  public void ByteArrayComparersOrderByContentWithPrefixesFirst()
  {
    byte[][] ordered = [[], [0], [0, 0], [0, 1], [1], [1, 0], [1, 0, 255], [1, 1], [255], [255, 255]];
    var ascending = new BytesLexicographicComparerAscending();
    var descending = new BytesLexicographicComparerDescending();
    for (var i = 0; i < ordered.Length; ++i)
    {
      for (var j = 0; j < ordered.Length; ++j)
      {
        Assert.That(Math.Sign(ascending.Compare(ordered[i], ordered[j])), Is.EqualTo(Math.Sign(i.CompareTo(j))));
        Assert.That(Math.Sign(descending.Compare(ordered[i], ordered[j])), Is.EqualTo(Math.Sign(j.CompareTo(i))));
      }
    }
    Assert.That(ascending.Compare(null, []), Is.Zero);
  }

  [Test]
  public void ByteArrayKeysHashByContent()
  {
    var hasher = new BytesKeyHasher();
    byte[] first = [1, 2, 3, 4];
    byte[] second = [1, 2, 3, 4];
    Assert.That(hasher.GetHashCode(first), Is.EqualTo(hasher.GetHashCode(second)));
    Assert.That(hasher.GetHashCode(null), Is.EqualTo(hasher.GetHashCode([])));
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void ByteArrayValueDeletionSurvivesSegmentChangesAndReload(DiskSegmentMode mode)
  {
    var provider = new InMemoryFileStreamProvider();

    using (var tree = CreateFactory<int, byte[]>(provider, mode).Create())
    {
      for (var key = 1; key <= 4; ++key)
        tree.Upsert(key, [(byte)key]);
      Merge(tree);
      Assert.That(tree.Maintenance.DiskSegment.Length, Is.EqualTo(4));

      tree.Upsert(5, [5]);
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Upsert(6, [6]);

      // Delete entries in disk, read-only, and mutable segments respectively.
      tree.ForceDelete(1);
      Assert.That(tree.TryDelete(5, out _), Is.True);
      tree.ForceDelete(6);
      tree.ForceDelete(9);
      AssertDeletions(tree);
      Assert.That(tree.TryDelete(5, out _), Is.False);

      tree.Maintenance.MoveMutableSegmentForward();
      AssertDeletions(tree);
      // Leave the deletions in the WAL for the first reopen.
    }

    using (var tree = CreateFactory<int, byte[]>(provider, mode).Open())
    {
      AssertDeletions(tree);
      Merge(tree);
      AssertDeletions(tree);
    }

    using (var tree = CreateFactory<int, byte[]>(provider, mode).Open())
    {
      AssertDeletions(tree);
      tree.Upsert(1, [42]);
      Assert.That(tree.TryGet(1, out var restored), Is.True);
      Assert.That(restored, Is.EqualTo(new byte[] { 42 }));
    }
  }

  [TestCase(false, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(false, DiskSegmentMode.MultiPartDiskSegment)]
  [TestCase(true, DiskSegmentMode.SingleDiskSegment)]
  [TestCase(true, DiskSegmentMode.MultiPartDiskSegment)]
  public void ByteArrayKeysSortAndSeekAcrossSegmentsAndReload(bool descending, DiskSegmentMode mode)
  {
    var provider = new InMemoryFileStreamProvider();
    byte[][] ordered = [[], [0], [1], [1, 0, 1], [1, 0, 2], [1, 1], [2]];

    ZoneTreeFactory<byte[], int> CreateKeyFactory()
    {
      var factory = CreateFactory<byte[], int>(provider, mode);
      if (descending)
        factory.SetComparer(new BytesLexicographicComparerDescending());
      return factory;
    }

    using (var tree = CreateKeyFactory().Create())
    {
      // Insert out of order to exercise tree placement as well as iteration.
      for (var i = ordered.Length - 1; i >= 0; --i)
        tree.Upsert(ordered[i], i + 1);
      AssertOrderAndSeeks(tree, ordered, descending);

      tree.Maintenance.MoveMutableSegmentForward();
      AssertOrderAndSeeks(tree, ordered, descending);
      tree.Maintenance.StartMergeOperation().Join();
      Assert.That(tree.Maintenance.DiskSegment.Length, Is.EqualTo(ordered.Length));
      AssertOrderAndSeeks(tree, ordered, descending);
    }

    using (var tree = CreateKeyFactory().Open())
      AssertOrderAndSeeks(tree, ordered, descending);
  }

  [Test]
  public void TransactionalTreesUseByteArrayKeysAndValuesWithoutConfiguration()
  {
    var provider = new InMemoryFileStreamProvider();
    ZoneTreeFactory<byte[], byte[]> CreateTransactionalFactory()
        => new ZoneTreeFactory<byte[], byte[]>(provider)
            .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.Shared);

    using (var tree = CreateTransactionalFactory().OpenOrCreateTransactional())
    {
      using (var transaction = tree.BeginTransaction())
      {
        transaction.Upsert([1, 2], [10]);
        transaction.Upsert([1], [20]);
        transaction.Upsert([3], [30]);
        transaction.Commit();
      }
      using (var transaction = tree.BeginTransaction())
      {
        transaction.Delete([3]);
        transaction.Commit();
      }
      tree.SyncWALAsync().GetAwaiter().GetResult();
    }

    using (var tree = CreateTransactionalFactory().OpenOrCreateTransactional())
    {
      using var read = tree.BeginReadOnlyTransaction();
      Assert.That(read.TryGet([1, 2], out var value) && value.SequenceEqual(new byte[] { 10 }), Is.True);
      Assert.That(read.ContainsKey([3]), Is.False);
      Assert.That(read.Range([0], [255]).Select(entry => entry.Key), Is.EqualTo(new[] { new byte[] { 1 }, new byte[] { 1, 2 } }));
    }
  }

  static void AssertDeletions(IZoneTree<int, byte[]> tree)
  {
    foreach (var key in new[] { 1, 5, 6, 9 })
    {
      Assert.That(tree.TryGet(key, out _), Is.False, $"Deleted key {key}");
      Assert.That(tree.ContainsKey(key), Is.False, $"Deleted key {key}");
    }

    for (var key = 2; key <= 4; ++key)
    {
      Assert.That(tree.TryGet(key, out var value), Is.True);
      Assert.That(value, Is.EqualTo(new byte[] { (byte)key }));
    }

    using var iterator = tree.CreateIterator();
    var keys = new List<int>();
    while (iterator.Next())
      keys.Add(iterator.CurrentKey);
    Assert.That(keys, Is.EqualTo(new[] { 2, 3, 4 }));
  }

  static void AssertOrderAndSeeks(IZoneTree<byte[], int> tree, byte[][] ordered, bool descending)
  {
    var expected = (descending ? ordered.Reverse() : ordered).ToArray();
    using (var iterator = tree.CreateIterator())
    {
      foreach (var key in expected)
      {
        Assert.That(iterator.Next(), Is.True);
        Assert.That(iterator.CurrentKey, Is.EqualTo(key));
      }
      Assert.That(iterator.Next(), Is.False);
    }

    using (var iterator = tree.CreateReverseIterator())
    {
      foreach (var key in expected.Reverse())
      {
        Assert.That(iterator.Next(), Is.True);
        Assert.That(iterator.CurrentKey, Is.EqualTo(key));
      }
      Assert.That(iterator.Next(), Is.False);
    }

    // An absent prefix exercises lower-bound seeks in both directions.
    byte[] absentPrefix = [1, 0];
    using (var iterator = tree.CreateIterator())
    {
      iterator.Seek(absentPrefix);
      Assert.That(iterator.Next(), Is.True);
      Assert.That(iterator.CurrentKey, Is.EqualTo(descending ? new byte[] { 1 } : new byte[] { 1, 0, 1 }));
    }

    using (var iterator = tree.CreateReverseIterator())
    {
      iterator.Seek(absentPrefix);
      Assert.That(iterator.Next(), Is.True);
      Assert.That(iterator.CurrentKey, Is.EqualTo(descending ? new byte[] { 1, 0, 1 } : new byte[] { 1 }));
    }

    // Lookups use a different array with the same content.
    foreach (var key in ordered)
      Assert.That(tree.TryGet(key.ToArray(), out _), Is.True);
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
