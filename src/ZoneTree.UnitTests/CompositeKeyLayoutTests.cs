using ZoneTree.AbstractFileStream;
using ZoneTree.Comparers;
using ZoneTree.Composite;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

/// <summary>Composite key layouts: key spaces whose keys always agree on each element's type and order.</summary>
public sealed class CompositeKeyLayoutTests
{
  static readonly BytesLexicographicComparerAscending Order = new();

  static readonly CompositeKeyLayout<string, long> ByCustomer = new(CompositeKey.Create("by-customer"));

  static readonly CompositeKeyLayout<DateTime, long> ByTime = new(CompositeKey.Create("by-time"), CompositeKeyOrder.Descending);

  static readonly CompositeKeyLayout<string, int, long> ByCity = new(CompositeKey.Create("by-city"));

  [Test]
  public void ALayoutCreatesAndDecodesItsKeys()
  {
    var key = ByCustomer.Create("bob", 42L);
    Assert.That(key, Is.EqualTo(CompositeKey.Create("by-customer", "bob", 42L)));
    Assert.That(ByCustomer.Decode(key), Is.EqualTo(("bob", 42L)));

    var placed = new DateTime(2026, 11, 15);
    var timeKey = ByTime.Create(placed, 7);
    Assert.That(timeKey, Is.EqualTo(CompositeKey.Create("by-time", CompositeKey.Descending(placed), 7L)));
    Assert.That(ByTime.Decode(timeKey), Is.EqualTo((placed, 7L)));

    Assert.That(ByTime.ElementCount, Is.EqualTo(2));
    Assert.That(ByTime.Order(0), Is.EqualTo(CompositeKeyOrder.Descending));
    Assert.That(ByTime.Order(1), Is.EqualTo(CompositeKeyOrder.Ascending));
    Assert.Throws<ArgumentOutOfRangeException>(() => ByTime.Order(2));
    Assert.That(ByTime.Prefix.ToArray(), Is.EqualTo(CompositeKey.Create("by-time")));
    Assert.That(ByTime.Matches(timeKey), Is.True);
    Assert.That(ByCustomer.Matches(timeKey), Is.False);

    // A layout without a prefix.
    var plain = new CompositeKeyLayout<int>([]);
    Assert.That(plain.Create(5), Is.EqualTo(CompositeKey.Create(5)));
    Assert.That(plain.Decode(CompositeKey.Create(5)), Is.EqualTo(5));
    // A layout's element can be nullable.
    var optional = new CompositeKeyLayout<string, int?>(CompositeKey.Create("optional"));
    Assert.That(optional.Decode(optional.Create("a", null)), Is.EqualTo(("a", (int?)null)));
  }

  [Test]
  public void ALayoutKeepsItsKeysInItsOrders()
  {
    var placed = new DateTime(2026, 11, 15);
    byte[][] newestFirst =
    [
      ByTime.Create(placed.AddDays(2), 1),
      ByTime.Create(placed.AddDays(1), 1),
      ByTime.Create(placed.AddDays(1), 2),
      ByTime.Create(placed, 0),
    ];
    AssertOrdered(newestFirst);
    AssertOrdered([ByCustomer.Create("alice", 9), ByCustomer.Create("bob", 1), ByCustomer.Create("bob", 2), ByCustomer.Create("bobby", 0)]);
  }

  [Test]
  public void ALayoutsRangesHoldExactlyItsKeys()
  {
    byte[][] keys =
    [
      ByCustomer.Create("alice", 1), ByCustomer.Create("bob", 1), ByCustomer.Create("bob", 2), ByCustomer.Create("bobby", 3),
      ByTime.Create(new DateTime(2026, 11, 15), 1), ByCity.Create("rome", 1, 1), ByCity.Create("rome", 2, 1), ByCity.Create("oslo", 1, 1),
      CompositeKey.Create("by-customers", "bob", 1L), CompositeKey.Create("by-customer\0", "bob", 1L), CompositeKey.Create("by-custome"),
    ];
    AssertHolds(ByCustomer.Range(), keys, [0, 1, 2, 3]);
    AssertHolds(ByCustomer.Range("bob"), keys, [1, 2]);
    AssertHolds(ByCity.Range(), keys, [5, 6, 7]);
    AssertHolds(ByCity.Range("rome"), keys, [5, 6]);
    AssertHolds(ByCity.Range("rome", 2), keys, [6]);
    AssertHolds(ByTime.Range(new DateTime(2026, 11, 15)), keys, [4]);
  }

  static void AssertHolds((byte[] From, byte[] To) range, byte[][] keys, int[] held)
  {
    for (var i = 0; i < keys.Length; ++i)
    {
      var inRange = Order.Compare(keys[i], range.From) >= 0 && Order.Compare(keys[i], range.To) <= 0;
      Assert.That(inRange, Is.EqualTo(held.Contains(i)), $"key {i}");
    }
  }

  [Test]
  public void ALayoutRejectsKeysOfOtherShapes()
  {
    var placed = new DateTime(2026, 11, 15);
    Assert.Throws<ArgumentException>(() => ByCustomer.Decode(ByTime.Create(placed, 1)));
    Assert.Throws<ArgumentException>(() => ByCustomer.Decode(CompositeKey.Create("by-customer", "bob", 1L, 2L)));
    Assert.Throws<ArgumentException>(() => ByCustomer.Decode(CompositeKey.Create("by-customer", "bob")));
    // A key that sorts its time ascending is not of the newest-first layout.
    var ascending = Assert.Throws<ArgumentException>(() => ByTime.Decode(CompositeKey.Create("by-time", placed, 1L)));
    Assert.That(ascending.Message, Does.Contain("sorts ascending, and the layout's sorts descending"));
    Assert.Throws<InvalidCastException>(() => ByCustomer.Decode(CompositeKey.Create("by-customer", 5, 1L)));
  }

  [Test]
  public void ALayoutIsCheckedWhenItIsMade()
  {
    Assert.Throws<NotSupportedException>(() => new CompositeKeyLayout<Version>([]));
    Assert.Throws<NotSupportedException>(() => new CompositeKeyLayout<string, Descending<int>>([]));
    Assert.Throws<ArgumentException>(() => new CompositeKeyLayout<int>([0x03, 0x61]));
    Assert.Throws<ArgumentOutOfRangeException>(() => new CompositeKeyLayout<int>([], (CompositeKeyOrder)5));
  }

  [Test]
  public void LayoutsOfEveryLengthCreateDecodeAndBound()
  {
    var guid = Guid.NewGuid();
    var layout = new CompositeKeyLayout<string, int, long, bool, Guid, decimal, DateOnly, byte[]>(
        CompositeKey.Create("eight"), CompositeKeyOrder.Ascending, CompositeKeyOrder.Descending, CompositeKeyOrder.Ascending,
        CompositeKeyOrder.Descending, CompositeKeyOrder.Ascending, CompositeKeyOrder.Descending, CompositeKeyOrder.Ascending,
        CompositeKeyOrder.Descending);
    var key = layout.Create("a", 2, 3L, true, guid, 1.5m, new DateOnly(2026, 11, 15), [9]);
    Assert.That(layout.Decode(key), Is.EqualTo(("a", 2, 3L, true, guid, 1.5m, new DateOnly(2026, 11, 15), new byte[] { 9 })));
    (byte[] From, byte[] To)[] ranges =
    [
      layout.Range(), layout.Range("a"), layout.Range("a", 2), layout.Range("a", 2, 3L), layout.Range("a", 2, 3L, true),
      layout.Range("a", 2, 3L, true, guid), layout.Range("a", 2, 3L, true, guid, 1.5m),
      layout.Range("a", 2, 3L, true, guid, 1.5m, new DateOnly(2026, 11, 15)),
    ];
    foreach (var (from, to) in ranges)
      Assert.That(Order.Compare(key, from) >= 0 && Order.Compare(key, to) <= 0, Is.True);
    Assert.That(Order.Compare(layout.Create("a", 3, 3L, true, guid, 1.5m, new DateOnly(2026, 11, 15), [9]), layout.Range("a", 2).To), Is.Negative);

    Assert.That(new CompositeKeyLayout<int, int, int>([]).Decode(CompositeKey.Create(1, 2, 3)), Is.EqualTo((1, 2, 3)));
    Assert.That(new CompositeKeyLayout<int, int, int, int>([]).Decode(CompositeKey.Create(1, 2, 3, 4)), Is.EqualTo((1, 2, 3, 4)));
    Assert.That(new CompositeKeyLayout<int, int, int, int, int>([]).Decode(CompositeKey.Create(1, 2, 3, 4, 5)), Is.EqualTo((1, 2, 3, 4, 5)));
    Assert.That(new CompositeKeyLayout<int, int, int, int, int, int>([]).Decode(CompositeKey.Create(1, 2, 3, 4, 5, 6)),
        Is.EqualTo((1, 2, 3, 4, 5, 6)));
    Assert.That(new CompositeKeyLayout<int, int, int, int, int, int, int>([]).Decode(CompositeKey.Create(1, 2, 3, 4, 5, 6, 7)),
        Is.EqualTo((1, 2, 3, 4, 5, 6, 7)));
  }

  [Test]
  public void AStringPositionOfALayoutBoundsTheStringsThatBeginWithText()
  {
    var byName = new CompositeKeyLayout<string, long>(CompositeKey.Create("by-name"), CompositeKeyOrder.Descending);
    byte[][] keys = [byName.Create("bo", 1), byName.Create("bob", 2), byName.Create("bp", 3), byName.Create("b", 4)];
    AssertHolds(CompositeKey.BeginsWith(byName.Prefix, "bo", byName.Order(0)), keys, [0, 1]);
  }

  static void AssertOrdered(byte[][] ordered)
  {
    for (var i = 1; i < ordered.Length; ++i)
      Assert.That(Order.Compare(ordered[i - 1], ordered[i]), Is.Negative, $"keys {i - 1} and {i}");
  }

  [TestCase(DiskSegmentMode.SingleDiskSegment)]
  [TestCase(DiskSegmentMode.MultiPartDiskSegment)]
  public void IndexesOfLayoutsScanThroughMergesAndReloads(DiskSegmentMode mode)
  {
    var provider = new InMemoryFileStreamProvider();
    var placed = new DateTime(2026, 11, 1);
    (long Id, string Customer, int Day)[] orders = [(1, "bob", 1), (2, "alice", 3), (3, "bob", 2), (4, "carol", 4), (5, "bob", 5)];
    using (var tree = CreateFactory(provider, mode).Create())
    {
      foreach (var (id, customer, day) in orders)
      {
        tree.Upsert(ByCustomer.Create(customer, id), [1]);
        tree.Upsert(ByTime.Create(placed.AddDays(day), id), [1]);
      }
      AssertScans(tree);
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Maintenance.StartMergeOperation().Join();
      AssertScans(tree);
    }
    using (var tree = CreateFactory(provider, mode).Open())
      AssertScans(tree);
  }

  static void AssertScans(IZoneTree<byte[], byte[]> tree)
  {
    Assert.That(Scan(tree, ByCustomer.Range("bob")).Select(key => ByCustomer.Decode(key).Item2), Is.EqualTo(new[] { 1L, 3L, 5L }));
    Assert.That(Scan(tree, ByTime.Range()).Select(key => ByTime.Decode(key).Item2), Is.EqualTo(new[] { 5L, 4L, 2L, 3L, 1L }));
  }

  static List<byte[]> Scan(IZoneTree<byte[], byte[]> tree, (byte[] From, byte[] To) range)
  {
    var keys = new List<byte[]>();
    using var iterator = tree.CreateIterator();
    iterator.Seek(range.From);
    while (iterator.Next() && Order.Compare(iterator.CurrentKey, range.To) <= 0)
      keys.Add(iterator.CurrentKey);
    return keys;
  }

  static ZoneTreeFactory<byte[], byte[]> CreateFactory(InMemoryFileStreamProvider provider, DiskSegmentMode mode)
  {
    return new ZoneTreeFactory<byte[], byte[]>(provider)
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.Sync)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DiskSegmentMode = mode;
          options.MinimumRecordCount = 2;
          options.MaximumRecordCount = 3;
        });
  }
}
