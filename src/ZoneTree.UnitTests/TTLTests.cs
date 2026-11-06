using ZoneTree.Comparers;
using ZoneTree.PresetTypes;
using ZoneTree.Serializers;

namespace ZoneTree.UnitTests;

/// <summary>
/// Values that expire, and expirations that slide.
/// </summary>
/// <remarks>
/// The checks follow the clock, not the length of a sleep: a loaded machine can sleep far
/// longer than asked. A check that a value is still alive runs only while the clock says it
/// is, and waits for an expiration last until the clock passed it.
/// </remarks>
public sealed class TTLTests
{
  static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(1);

  /// <summary>Waits until the clock reaches the time, when a value with that expiration expired.</summary>
  static void WaitUntil(DateTime time)
  {
    while (DateTime.UtcNow < time)
      Thread.Sleep(10);
  }

  [Test]
  public void TestTTL()
  {
    var dataPath = "data/TestTTL";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var zoneTree = new ZoneTreeFactory<int, TTLValue<int>>()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .SetValueSerializer(new StructSerializer<TTLValue<int>>())
        .SetIsDeletedDelegate((in int key, in TTLValue<int> value) => value.IsExpired)
        .SetMarkValueDeletedDelegate(void (ref TTLValue<int> value) => value.Expire())
        .OpenOrCreate();

    // A value is found until it expires.
    var expiration = DateTime.UtcNow + Lifetime;
    zoneTree.Upsert(5, new TTLValue<int>(99, expiration));
    var found = zoneTree.TryGet(5, out var value);
    if (DateTime.UtcNow < expiration)
      Assert.That(found && value.Value == 99, Is.True);
    WaitUntil(expiration);
    Assert.That(zoneTree.TryGet(5, out _), Is.False);

    // Sliding the expiration keeps the value past its first expiration.
    var firstExpiration = DateTime.UtcNow + Lifetime;
    zoneTree.Upsert(5, new TTLValue<int>(99, firstExpiration));
    WaitUntil(firstExpiration - Lifetime / 2);
    var slideStart = DateTime.UtcNow;
    var slid = zoneTree.TryGetAndUpdate(
        5,
        out _,
        bool (ref TTLValue<int> v) => v.SlideExpiration(Lifetime),
        out _);
    // The slid expiration is the slide's clock plus the lifetime.
    var slideEnd = DateTime.UtcNow;
    if (slid)
    {
      WaitUntil(firstExpiration);
      found = zoneTree.TryGet(5, out _);
      if (DateTime.UtcNow < slideStart + Lifetime)
        Assert.That(found, Is.True);
    }
    else
    {
      // Only a value that expired before the slide is not found.
      Assert.That(slideEnd, Is.GreaterThanOrEqualTo(firstExpiration));
    }

    // Once the slid expiration passed, the value is gone and does not slide again.
    WaitUntil(slideEnd + Lifetime);
    var slidAgain = zoneTree.TryGetAndUpdate(
        5,
        out _,
        bool (ref TTLValue<int> v) => v.SlideExpiration(Lifetime),
        out _);
    Assert.That(slidAgain, Is.False);

    zoneTree.Maintenance.Drop();
  }
}
