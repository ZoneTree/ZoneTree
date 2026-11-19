using ZoneTree.AbstractFileStream;
using ZoneTree.Comparers;
using ZoneTree.Composite;
using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Transactional;

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

  [Test]
  public void ALayoutsBetweenHoldsTheKeysFromOneValueThroughAnother()
  {
    var placed = new DateTime(2026, 11, 1);
    byte[][] keys =
    [
      ByTime.Create(placed.AddDays(1), 1), ByTime.Create(placed.AddDays(2), 2), ByTime.Create(placed.AddDays(3), 3),
      ByTime.Create(placed.AddDays(4), 4), ByTime.Create(placed.AddDays(5), 5),
      ByCustomer.Create("alice", 1), ByCustomer.Create("bob", 1), ByCustomer.Create("bob", 2), ByCustomer.Create("bob", 3),
      ByCustomer.Create("bobby", 1), ByCustomer.Create("carol", 1),
      ByCity.Create("rome", 1, 1), ByCity.Create("rome", 2, 1), ByCity.Create("rome", 3, 1), ByCity.Create("oslo", 2, 1),
    ];
    // A newest-first position takes its values in either order, as an ascending one does.
    AssertHolds(ByTime.Between(placed.AddDays(2), placed.AddDays(4)), keys, [1, 2, 3]);
    AssertHolds(ByTime.Between(placed.AddDays(4), placed.AddDays(2)), keys, [1, 2, 3]);
    AssertHolds(ByCustomer.Between("alice", "bob"), keys, [5, 6, 7, 8]);
    AssertHolds(ByCustomer.Between("bob", "alice"), keys, [5, 6, 7, 8]);
    AssertHolds(ByCustomer.Between("bob", 2, 3), keys, [7, 8]);
    AssertHolds(ByCustomer.Between("bob", 2, 2), keys, [7]);
    AssertHolds(ByCity.Between("rome", 1, 2), keys, [11, 12]);
    AssertHolds(ByCity.Between("rome", 2, 1, 1), keys, [12]);

    // The bounds exclude a value by its argument, also where it is a whole key, and on a newest-first position.
    AssertHolds(ByCustomer.Between("bob", 1, 3, CompositeKeyBounds.ExcludeHigh), keys, [6, 7]);
    AssertHolds(ByCustomer.Between("bob", 1, 3, CompositeKeyBounds.ExcludeLow), keys, [7, 8]);
    AssertHolds(ByCustomer.Between("bob", 1, 3, CompositeKeyBounds.Exclusive), keys, [7]);
    AssertHolds(ByCustomer.Between("alice", "bob", CompositeKeyBounds.ExcludeHigh), keys, [5]);
    AssertHolds(ByTime.Between(placed.AddDays(2), placed.AddDays(4), CompositeKeyBounds.ExcludeHigh), keys, [1, 2]);
    AssertHolds(ByTime.Between(placed.AddDays(2), placed.AddDays(4), CompositeKeyBounds.ExcludeLow), keys, [2, 3]);
    AssertHolds(ByCity.Between("rome", 1, 3, CompositeKeyBounds.Exclusive), keys, [12]);
    Assert.Throws<ArgumentOutOfRangeException>(() => ByCustomer.Between("a", "b", (CompositeKeyBounds)8));
  }

  static void AssertHolds(CompositeKeyRange range, byte[][] keys, int[] held)
  {
    for (var i = 0; i < keys.Length; ++i)
    {
      Assert.That(range.Contains(keys[i]), Is.EqualTo(held.Contains(i)), $"key {i}");
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
    Assert.That(ascending.Message, Is.EqualTo("Element 1 sorts ascending, and the layout's sorts descending."));
    Assert.Throws<InvalidCastException>(() => ByCustomer.Decode(CompositeKey.Create("by-customer", 5, 1L)));

    // An error names the element by its index in the whole key, and malformed bytes by their offset in it, as
    // CompositeKey.Get does: the prefix's element is 0, so the customer is 1 and the order id 2.
    var integer = Assert.Throws<InvalidCastException>(() => ByCustomer.Decode(CompositeKey.Create("by-customer", "bob", "1")));
    Assert.That(integer.Message, Does.StartWith("Element 2 is String"));
    byte[] notUtf8 = [.. CompositeKey.Create("by-customer"), 0x03, 0xC3, 0x28, 0x00, 0x01, .. CompositeKey.Create(1L)];
    var text = Assert.Throws<ArgumentException>(() => ByCustomer.Decode(notUtf8));
    Assert.That(text.Message, Is.EqualTo(Assert.Throws<ArgumentException>(() => CompositeKey.Get<string>(notUtf8, 1)).Message));
    Assert.That(text.Message, Does.StartWith("Element 1 is a string whose bytes are not valid UTF-8"));
    var customer = CompositeKey.Create("by-customer", "bob");
    byte[] malformed = [.. customer, 0xFF];
    var bytes = Assert.Throws<ArgumentException>(() => ByCustomer.Decode(malformed));
    Assert.That(bytes.Message, Is.EqualTo(Assert.Throws<ArgumentException>(() => CompositeKey.Get<long>(malformed, 2)).Message));
    Assert.That(bytes.Message, Does.Contain($"the element at byte {customer.Length} is malformed"));

    // TryDecode returns false where Decode throws, and leaves the default behind.
    Assert.That(ByCustomer.TryDecode(ByCustomer.Create("bob", 1), out var elements), Is.True);
    Assert.That(elements, Is.EqualTo(("bob", 1L)));
    byte[][] others =
    [
      ByTime.Create(placed, 1), CompositeKey.Create("by-customer", "bob", 1L, 2L), CompositeKey.Create("by-customer", "bob"),
      CompositeKey.Create("by-customer", 5, 1L), CompositeKey.Create("by-customer", "bob", "1"), [.. CompositeKey.Create("by-customer", "bob"), 0xFF],
      CompositeKey.Create("by-customer", CompositeKey.Descending("bob"), 1L), [],
    ];
    foreach (var other in others)
    {
      Assert.That(ByCustomer.TryDecode(other, out elements), Is.False, CompositeKey.Format(other));
      Assert.That(elements, Is.EqualTo(default((string, long))));
    }
    Assert.That(ByTime.TryDecode(CompositeKey.Create("by-time", placed, 1L), out _), Is.False);
    var plain = new CompositeKeyLayout<int>([]);
    Assert.That(plain.TryDecode(CompositeKey.Create(5), out var five), Is.True);
    Assert.That(five, Is.EqualTo(5));
    Assert.That(plain.TryDecode(CompositeKey.Create(5L, 6), out five), Is.False);
    Assert.That(five, Is.Zero);
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
    CompositeKeyRange[] ranges =
    [
      layout.Range(), layout.Range("a"), layout.Range("a", 2), layout.Range("a", 2, 3L), layout.Range("a", 2, 3L, true),
      layout.Range("a", 2, 3L, true, guid), layout.Range("a", 2, 3L, true, guid, 1.5m),
      layout.Range("a", 2, 3L, true, guid, 1.5m, new DateOnly(2026, 11, 15)),
    ];
    foreach (var range in ranges)
      Assert.That(range.Contains(key), Is.True, $"{range}");
    Assert.That(Order.Compare(layout.Create("a", 3, 3L, true, guid, 1.5m, new DateOnly(2026, 11, 15), [9]), layout.Range("a", 2).Upper), Is.Negative);

    // Between at every position, ascending and descending alike, holds the key and not one past its values.
    var day = new DateOnly(2026, 11, 15);
    CompositeKeyRange[] betweens =
    [
      layout.Between("a", "b"), layout.Between("a", 1, 3), layout.Between("a", 2, 2L, 4L), layout.Between("a", 2, 3L, false, true),
      layout.Between("a", 2, 3L, true, Guid.Empty, guid), layout.Between("a", 2, 3L, true, guid, 1m, 2m),
      layout.Between("a", 2, 3L, true, guid, 1.5m, day.AddDays(-1), day.AddDays(1)),
      layout.Between("a", 2, 3L, true, guid, 1.5m, day, [8], [10]),
    ];
    foreach (var between in betweens)
      Assert.That(between.Contains(key), Is.True, $"{between}");
    var past = layout.Create("a", 4, 3L, true, guid, 1.5m, day, [9]);
    Assert.That(betweens[1].Contains(past), Is.False);

    // The one-sided ranges at every position are those of the key's elements through that position.
    byte[][] bounds =
    [
      layout.Range("a").Lower, layout.Range("a", 2).Lower, layout.Range("a", 2, 3L).Lower, layout.Range("a", 2, 3L, true).Lower,
      layout.Range("a", 2, 3L, true, guid).Lower, layout.Range("a", 2, 3L, true, guid, 1.5m).Lower,
      layout.Range("a", 2, 3L, true, guid, 1.5m, day).Lower, key,
    ];
    CompositeKeyRange[] atLeast =
    [
      layout.AtLeast("a"), layout.AtLeast("a", 2), layout.AtLeast("a", 2, 3L), layout.AtLeast("a", 2, 3L, true),
      layout.AtLeast("a", 2, 3L, true, guid), layout.AtLeast("a", 2, 3L, true, guid, 1.5m),
      layout.AtLeast("a", 2, 3L, true, guid, 1.5m, day), layout.AtLeast("a", 2, 3L, true, guid, 1.5m, day, [9]),
    ];
    CompositeKeyRange[] greaterThan =
    [
      layout.GreaterThan("a"), layout.GreaterThan("a", 2), layout.GreaterThan("a", 2, 3L), layout.GreaterThan("a", 2, 3L, true),
      layout.GreaterThan("a", 2, 3L, true, guid), layout.GreaterThan("a", 2, 3L, true, guid, 1.5m),
      layout.GreaterThan("a", 2, 3L, true, guid, 1.5m, day), layout.GreaterThan("a", 2, 3L, true, guid, 1.5m, day, [9]),
    ];
    CompositeKeyRange[] atMost =
    [
      layout.AtMost("a"), layout.AtMost("a", 2), layout.AtMost("a", 2, 3L), layout.AtMost("a", 2, 3L, true),
      layout.AtMost("a", 2, 3L, true, guid), layout.AtMost("a", 2, 3L, true, guid, 1.5m),
      layout.AtMost("a", 2, 3L, true, guid, 1.5m, day), layout.AtMost("a", 2, 3L, true, guid, 1.5m, day, [9]),
    ];
    CompositeKeyRange[] lessThan =
    [
      layout.LessThan("a"), layout.LessThan("a", 2), layout.LessThan("a", 2, 3L), layout.LessThan("a", 2, 3L, true),
      layout.LessThan("a", 2, 3L, true, guid), layout.LessThan("a", 2, 3L, true, guid, 1.5m),
      layout.LessThan("a", 2, 3L, true, guid, 1.5m, day), layout.LessThan("a", 2, 3L, true, guid, 1.5m, day, [9]),
    ];
    for (var i = 0; i < bounds.Length; ++i)
    {
      Assert.That(atLeast[i], Is.EqualTo(CompositeKey.AtLeast(bounds[i])), $"at least, position {i}");
      Assert.That(greaterThan[i], Is.EqualTo(CompositeKey.GreaterThan(bounds[i])), $"greater than, position {i}");
      Assert.That(atMost[i], Is.EqualTo(CompositeKey.AtMost(bounds[i])), $"at most, position {i}");
      Assert.That(lessThan[i], Is.EqualTo(CompositeKey.LessThan(bounds[i])), $"less than, position {i}");
      Assert.That(atLeast[i].Contains(key) && atMost[i].Contains(key), Is.True, $"position {i}");
      Assert.That(greaterThan[i].Contains(key) || lessThan[i].Contains(key), Is.False, $"position {i}");
    }

    // BeginsWith at the string position and at the descending bytes one, in the layout's orders.
    Assert.That(layout.BeginsWith("a"), Is.EqualTo(CompositeKey.BeginsWith(layout.Prefix, "a", layout.Order(0))));
    Assert.That(layout.BeginsWith("a", 2, 3L, true, guid, 1.5m, day, new byte[] { 9 }),
        Is.EqualTo(CompositeKey.BeginsWith(bounds[6], new byte[] { 9 }, layout.Order(7))));
    Assert.That(layout.BeginsWith("a", 2, 3L, true, guid, 1.5m, day, new byte[] { 9 }).Contains(key), Is.True);
    AssertMismatch(() => layout.BeginsWith("a", "2"), 1, typeof(int), CompositeElementType.String);
    AssertMismatch(() => layout.BeginsWith(new byte[] { 1 }), 0, typeof(string), CompositeElementType.Bytes);
    Assert.Throws<ArgumentNullException>(() => layout.BeginsWith((string)null));
    Assert.That(layout.TryDecode(key, out var eight), Is.True);
    Assert.That(eight, Is.EqualTo(("a", 2, 3L, true, guid, 1.5m, day, new byte[] { 9 })));

    Assert.That(new CompositeKeyLayout<int, int, int>([]).Decode(CompositeKey.Create(1, 2, 3)), Is.EqualTo((1, 2, 3)));
    Assert.That(new CompositeKeyLayout<int, int, int>([]).TryDecode(CompositeKey.Create(1, 2, 3), out var three), Is.True);
    Assert.That(three, Is.EqualTo((1, 2, 3)));
    Assert.That(new CompositeKeyLayout<int, int, int, int>([]).TryDecode(CompositeKey.Create(1, 2, 3, 4), out var four), Is.True);
    Assert.That(four, Is.EqualTo((1, 2, 3, 4)));
    Assert.That(new CompositeKeyLayout<int, int, int, int, int>([]).TryDecode(CompositeKey.Create(1, 2, 3, 4, 5), out var five), Is.True);
    Assert.That(five, Is.EqualTo((1, 2, 3, 4, 5)));
    Assert.That(new CompositeKeyLayout<int, int, int, int, int, int>([]).TryDecode(CompositeKey.Create(1, 2, 3, 4, 5, 6), out var six),
        Is.True);
    Assert.That(six, Is.EqualTo((1, 2, 3, 4, 5, 6)));
    Assert.That(new CompositeKeyLayout<int, int, int, int, int, int, int>([]).TryDecode(CompositeKey.Create(1, 2, 3, 4, 5, 6, 7),
        out var seven), Is.True);
    Assert.That(seven, Is.EqualTo((1, 2, 3, 4, 5, 6, 7)));
    Assert.That(new CompositeKeyLayout<int, int, int, int>([]).Decode(CompositeKey.Create(1, 2, 3, 4)), Is.EqualTo((1, 2, 3, 4)));
    Assert.That(new CompositeKeyLayout<int, int, int, int, int>([]).Decode(CompositeKey.Create(1, 2, 3, 4, 5)), Is.EqualTo((1, 2, 3, 4, 5)));
    Assert.That(new CompositeKeyLayout<int, int, int, int, int, int>([]).Decode(CompositeKey.Create(1, 2, 3, 4, 5, 6)),
        Is.EqualTo((1, 2, 3, 4, 5, 6)));
    Assert.That(new CompositeKeyLayout<int, int, int, int, int, int, int>([]).Decode(CompositeKey.Create(1, 2, 3, 4, 5, 6, 7)),
        Is.EqualTo((1, 2, 3, 4, 5, 6, 7)));
  }

  [Test]
  public void ALayoutsStringAndBytesPositionsHoldWhatBeginsWithTextOrBytes()
  {
    var byName = new CompositeKeyLayout<string, long>(CompositeKey.Create("by-name"), CompositeKeyOrder.Descending);
    byte[][] keys = [byName.Create("bo", 1), byName.Create("bob", 2), byName.Create("bp", 3), byName.Create("b", 4)];
    AssertHolds(byName.BeginsWith("bo"), keys, [0, 1]);

    // At a later position, after the elements before it.
    var byTenant = new CompositeKeyLayout<string, string>(
        CompositeKey.Create("by-tenant"), CompositeKeyOrder.Ascending, CompositeKeyOrder.Descending);
    byte[][] tenantKeys = [byTenant.Create("acme", "bo"), byTenant.Create("acme", "bob"), byTenant.Create("acme", "bp"), byTenant.Create("zeta", "bo")];
    AssertHolds(byTenant.BeginsWith("acme", "bo"), tenantKeys, [0, 1]);
    var byHash = new CompositeKeyLayout<string, byte[]>(CompositeKey.Create("by-hash"));
    byte[][] hashKeys = [byHash.Create("a", [1, 2]), byHash.Create("a", [1]), byHash.Create("a", [2]), byHash.Create("b", [1])];
    AssertHolds(byHash.BeginsWith("a", new byte[] { 1 }), hashKeys, [0, 1]);

    AssertMismatch(() => byName.BeginsWith("bo", "x"), 1, typeof(long), CompositeElementType.String);
    AssertMismatch(() => byName.BeginsWith(new byte[] { 1 }), 0, typeof(string), CompositeElementType.Bytes);
    AssertMismatch(() => byHash.BeginsWith("a", "x"), 1, typeof(byte[]), CompositeElementType.String);
    Assert.Throws<ArgumentNullException>(() => byName.BeginsWith((string)null));
  }

  /// <summary>Checks that a BeginsWith call throws for the position, its type, and the element type the range needs.</summary>
  static void AssertMismatch(Action call, int position, Type positionType, CompositeElementType required)
  {
    var mismatch = Assert.Throws<PositionTypeMismatchException>(call);
    Assert.That((mismatch.Position, mismatch.PositionType, mismatch.RequiredElementType),
        Is.EqualTo((position, positionType, required)));
  }

  [TestCase(CompositeKeyOrder.Ascending)]
  [TestCase(CompositeKeyOrder.Descending)]
  public void OneSidedRangesHoldWhatABetweenWithOneEndHolds(CompositeKeyOrder order)
  {
    // A null sorts below every value, so at most 5 holds it, as a Between from null through 5 does.
    var scores = new CompositeKeyLayout<int?>([], order);
    byte[][] keys = [scores.Create(null), scores.Create(3), scores.Create(8)];
    AssertHolds(scores.AtMost(5), keys, [0, 1]);
    AssertHolds(scores.Between(null, 5), keys, [0, 1]);
    AssertHolds(scores.LessThan(3), keys, [0]);
    AssertHolds(scores.AtLeast(5), keys, [2]);
    AssertHolds(scores.GreaterThan(3), keys, [2]);
    AssertHolds(scores.GreaterThan(null), keys, [1, 2]);
    AssertHolds(scores.AtLeast(null), keys, [0, 1, 2]);
    AssertHolds(scores.AtMost(null), keys, [0]);
    AssertHolds(scores.LessThan(null), keys, []);

    // After a leading element, the same holds within that element's keys only.
    var byTeam = new CompositeKeyLayout<string, int?>(CompositeKey.Create("by-team"), CompositeKeyOrder.Ascending, order);
    byte[][] teamKeys = [byTeam.Create("a", null), byTeam.Create("a", 3), byTeam.Create("a", 8), byTeam.Create("b", null), byTeam.Create("b", 1)];
    AssertHolds(byTeam.AtMost("a", 5), teamKeys, [0, 1]);
    AssertHolds(byTeam.Between("a", null, 5), teamKeys, [0, 1]);
    AssertHolds(byTeam.GreaterThan("a", null), teamKeys, [1, 2]);
    AssertHolds(byTeam.LessThan("b", 1), teamKeys, [3]);
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
    long Customer(byte[] key) => ByCustomer.Decode(key).Item2;
    long Time(byte[] key) => ByTime.Decode(key).Item2;
    AssertIds(tree, ByCustomer.Range("bob"), Customer, 1, 3, 5);
    AssertIds(tree, ByTime.Range(), Time, 5, 4, 2, 3, 1);
    // Orders placed from the second day through the fourth, newest first, and those of alice through bob.
    var placed = new DateTime(2026, 11, 1);
    AssertIds(tree, ByTime.Between(placed.AddDays(2), placed.AddDays(4)), Time, 4, 2, 3);
    AssertIds(tree, ByCustomer.Between("alice", "bob"), Customer, 2, 1, 3, 5);
    // Up to, but not at, the high value: the fourth day, and bob's order 5, whose key is the excluded value's own.
    AssertIds(tree, ByTime.Between(placed.AddDays(2), placed.AddDays(4), CompositeKeyBounds.ExcludeHigh), Time, 2, 3);
    AssertIds(tree, ByCustomer.Between("bob", 1L, 5L, CompositeKeyBounds.ExcludeHigh), Customer, 1, 3);
    // From, but not at, the low value, which a newest-first position sorts last.
    AssertIds(tree, ByTime.Between(placed.AddDays(2), placed.AddDays(4), CompositeKeyBounds.ExcludeLow), Time, 4, 2);

    // One-sided ranges compare values, on the newest-first position as on the ascending one.
    var third = placed.AddDays(3);
    AssertIds(tree, ByTime.AtLeast(third), Time, 5, 4, 2);
    AssertIds(tree, ByTime.GreaterThan(third), Time, 5, 4);
    AssertIds(tree, ByTime.AtMost(third), Time, 2, 3, 1);
    AssertIds(tree, ByTime.LessThan(third), Time, 3, 1);
    AssertIds(tree, ByCustomer.AtLeast("bob", 3L), Customer, 3, 5);
    AssertIds(tree, ByCustomer.GreaterThan("bob", 3L), Customer, 5);
    AssertIds(tree, ByCustomer.AtMost("bob", 3L), Customer, 1, 3);
    AssertIds(tree, ByCustomer.LessThan("bob", 3L), Customer, 1);
    AssertIds(tree, ByCustomer.AtLeast("bob"), Customer, 1, 3, 5, 4);
    AssertIds(tree, ByCustomer.LessThan("bob"), Customer, 2);
    AssertIds(tree, ByCustomer.BeginsWith("b"), Customer, 1, 3, 5);

    // Page by page, forward and backward.
    AssertPages(tree, false, ByTime.Range(), Time, 5, 4, 2, 3, 1);
    AssertPages(tree, false, ByCustomer.Range("bob"), Customer, 1, 3, 5);
  }

  /// <summary>
  /// Reads a range two keys at a time with each iterator, each page continuing from the last key of the page before:
  /// a page read upward seeks the lower bound and the next starts after its highest key, and one read downward seeks
  /// the upper bound and the next ends before its lowest key. On a tree that sorts keys descending, a forward
  /// iterator reads downward and a reverse one upward. The ids are those of the range's keys in byte order.
  /// </summary>
  static void AssertPages(
      IZoneTree<byte[], byte[]> tree, bool descendingTree, CompositeKeyRange range, Func<byte[], long> id, params long[] ids)
  {
    foreach (var forward in new[] { true, false })
    {
      var upward = forward != descendingTree;
      var read = new List<long>();
      for (var page = range; ;)
      {
        var iterator = forward ? tree.CreateIterator() : tree.CreateReverseIterator();
        var keys = Walk(iterator, upward ? page.Lower : page.Upper, page).Take(2).ToList();
        if (keys.Count == 0)
          break;
        read.AddRange(keys.Select(id));
        page = upward ? page.StartAfter(keys[^1]) : page.EndBefore(keys[^1]);
      }
      Assert.That(read, Is.EqualTo(upward ? ids : Enumerable.Reverse(ids)), $"{(forward ? "forward" : "reverse")} {range}");
    }
  }

  [Test]
  public void EveryRangeScansUpwardAndDownwardWithOneCheck()
  {
    var byId = new CompositeKeyLayout<string, long>(CompositeKey.Create("by-id"));
    var newestId = new CompositeKeyLayout<string, long>(
        CompositeKey.Create("newest-id"), CompositeKeyOrder.Ascending, CompositeKeyOrder.Descending);
    using var tree = CreateFactory(new InMemoryFileStreamProvider(), DiskSegmentMode.SingleDiskSegment).Create();
    using var descendingTree = CreateFactory(new InMemoryFileStreamProvider(), DiskSegmentMode.SingleDiskSegment)
        .SetComparer(new BytesLexicographicComparerDescending())
        .Create();
    foreach (var target in new[] { tree, descendingTree })
    {
      foreach (var customer in new[] { "alice", "bob", "bobby" })
      {
        for (var id = 1L; id <= 5; ++id)
        {
          target.Upsert(byId.Create(customer, id), [1]);
          target.Upsert(newestId.Create(customer, id), [1]);
        }
      }
    }

    (CompositeKeyBounds Bounds, long[] Ids)[] cases =
    [
      (CompositeKeyBounds.Inclusive, [2, 3, 4]), (CompositeKeyBounds.ExcludeLow, [3, 4]),
      (CompositeKeyBounds.ExcludeHigh, [2, 3]), (CompositeKeyBounds.Exclusive, [3]),
    ];
    foreach (var (bounds, ids) in cases)
    {
      // Every excluded id is a whole key, at the last position, ascending and descending.
      var ascending = byId.Between("bob", 2L, 4L, bounds);
      var descending = newestId.Between("bob", 2L, 4L, bounds);
      var reversed = Enumerable.Reverse(ids).ToArray();
      AssertIds(tree, ascending, key => byId.Decode(key).Item2, ids);
      AssertIds(tree, descending, key => newestId.Decode(key).Item2, reversed);

      // On a tree that sorts keys descending, a forward iterator walks down, and a reverse one up.
      Assert.That(Walk(descendingTree.CreateIterator(), ascending.Upper, ascending).Select(key => byId.Decode(key).Item2),
          Is.EqualTo(reversed), $"{bounds}");
      Assert.That(Walk(descendingTree.CreateReverseIterator(), ascending.Lower, ascending).Select(key => byId.Decode(key).Item2),
          Is.EqualTo(ids), $"{bounds}");
    }

    // Page by page on both trees, with each iterator, at an ascending and a descending position.
    foreach (var (target, isDescending) in new[] { (tree, false), (descendingTree, true) })
    {
      AssertPages(target, isDescending, byId.Range("bob"), key => byId.Decode(key).Item2, 1, 2, 3, 4, 5);
      AssertPages(target, isDescending, newestId.Range("bob"), key => newestId.Decode(key).Item2, 5, 4, 3, 2, 1);
    }
  }

  /// <summary>Scans a range upward from its lower bound and downward from its upper bound, with the same check.</summary>
  static void AssertIds(IZoneTree<byte[], byte[]> tree, CompositeKeyRange range, Func<byte[], long> id, params long[] ids)
  {
    Assert.That(Walk(tree.CreateIterator(), range.Lower, range).Select(id), Is.EqualTo(ids), $"up {range}");
    Assert.That(Walk(tree.CreateReverseIterator(), range.Upper, range).Select(id), Is.EqualTo(Enumerable.Reverse(ids)), $"down {range}");
  }

  /// <summary>The keys of a range from where an iterator seeks, while the range contains them.</summary>
  static List<byte[]> Walk(IZoneTreeIterator<byte[], byte[]> iterator, byte[] start, CompositeKeyRange range)
  {
    using (iterator)
    {
      var keys = new List<byte[]>();
      iterator.Seek(start);
      while (iterator.Next() && range.Contains(iterator.CurrentKey))
        keys.Add(iterator.CurrentKey);
      return keys;
    }
  }

  [Test]
  public void TransactionsReadCompositeRangesAndSerializableOnesGuardExactlyThem()
  {
    using var tree = CreateFactory(new InMemoryFileStreamProvider(), DiskSegmentMode.SingleDiskSegment).CreateTransactional();
    var byCustomer = new CompositeKeyLayout<string, long>(CompositeKey.Create("by-customer"));
    byte[] entry = [1];
    long Id(byte[] key) => byCustomer.Decode(key).Item2;
    void Order(string customer, long id)
    {
      using var tx = tree.BeginTransaction();
      tx.Upsert(byCustomer.Create(customer, id), entry);
      tx.Commit();
    }
    foreach (var (customer, id) in new[] { ("alice", 1L), ("bob", 2L), ("bob", 4L), ("bobby", 3L), ("carol", 5L) })
      Order(customer, id);

    var bob = byCustomer.Range("bob");
    using (var tx = tree.BeginTransaction(TransactionIsolationLevel.Snapshot))
    {
      // The transaction's own write, not yet committed, is among the keys it reads.
      tx.Upsert(byCustomer.Create("bob", 3), entry);
      Assert.That(Read(tx, bob, Id), Is.EqualTo(new long[] { 2, 3, 4 }));
      Assert.That(Read(tx, bob, Id, reverse: true), Is.EqualTo(new long[] { 4, 3, 2 }));
      // Range takes the bounds as they are: Upper is never a key, so its inclusive end holds no extra key.
      Assert.That(tx.Range(bob.Lower, bob.Upper).Select(record => Id(record.Key)), Is.EqualTo(new long[] { 2, 3, 4 }));
      Assert.That(tx.Range(bob.Lower, bob.Upper, reverse: true).Select(record => Id(record.Key)), Is.EqualTo(new long[] { 4, 3, 2 }));
      Assert.That(Read(tx, bob.StartAfter(byCustomer.Create("bob", 2)), Id), Is.EqualTo(new long[] { 3, 4 }));
      Assert.That(Read(tx, bob.EndBefore(byCustomer.Create("bob", 4)), Id, reverse: true), Is.EqualTo(new long[] { 3, 2 }));
      Assert.That(Read(tx, byCustomer.BeginsWith("bob"), Id), Is.EqualTo(new long[] { 2, 3, 4, 3 }));
      tx.Commit();
    }

    // A serializable read of a range conflicts with a commit inside it, and with none outside it: not another
    // customer's whose name starts with bob, nor the excluded end of a range, which is a whole key.
    bool CommitsAfterAnOrderOf(CompositeKeyRange range, string customer, long id)
    {
      using var reader = tree.BeginTransaction(TransactionIsolationLevel.Serializable);
      reader.Range(range.Lower, range.Upper).ToList();
      Order(customer, id);
      reader.Upsert(CompositeKey.Create("audit"), entry);
      return reader.TryCommit();
    }
    var upToTen = byCustomer.Between("bob", 0L, 10L, CompositeKeyBounds.ExcludeHigh);
    Assert.That(CommitsAfterAnOrderOf(bob, "bobby", 6), Is.True);
    Assert.That(CommitsAfterAnOrderOf(bob, "alice", 7), Is.True);
    Assert.That(CommitsAfterAnOrderOf(upToTen, "bob", 10), Is.True);
    Assert.That(CommitsAfterAnOrderOf(upToTen, "bob", 8), Is.False);
    Assert.That(CommitsAfterAnOrderOf(bob, "bob", 9), Is.False);
  }

  /// <summary>The ids of a range's keys a transaction reads, seeking one bound and reading while the range holds them.</summary>
  static List<long> Read(IReadTransaction<byte[], byte[]> tx, CompositeKeyRange range, Func<byte[], long> id, bool reverse = false)
  {
    using var iterator = reverse ? tx.CreateReverseIterator() : tx.CreateIterator();
    iterator.Seek(reverse ? range.Upper : range.Lower);
    var ids = new List<long>();
    while (iterator.Next() && range.Contains(iterator.CurrentKey))
      ids.Add(id(iterator.CurrentKey));
    return ids;
  }

  static ZoneTreeFactory<byte[], byte[]> CreateFactory(InMemoryFileStreamProvider provider, DiskSegmentMode mode)
  {
    return new ZoneTreeFactory<byte[], byte[]>(provider)
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .ConfigureWriteAheadLogOptions(options => options.Mode = WriteAheadLogMode.Private)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.Mode = mode;
          options.MinimumRecordCount = 2;
          options.MaximumRecordCount = 3;
        });
  }
}
