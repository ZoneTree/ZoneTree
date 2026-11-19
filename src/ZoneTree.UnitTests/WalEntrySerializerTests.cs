using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

/// <summary>
/// Tests the buffers that disk segments and write-ahead logs serialize keys and values into, and the log records
/// built in the calling thread's buffer.
/// </summary>
public sealed class WalEntrySerializerTests
{
  static readonly Int64Serializer Int64 = new();

  static readonly Utf8StringSerializer Utf8 = new();

  [Test]
  public void ABufferIsReusedUpTo64KBAndALongerOneIsNotKept()
  {
    var buffer = new SerializationBuffer();
    var first = buffer.Get(100);
    Assert.That(buffer.Get(10) == first, Is.True, "a shorter entry reuses the buffer");
    Assert.That(buffer.Get(first.Length) == first, Is.True, "an entry of its length reuses the buffer");
    var grown = buffer.Get(first.Length + 1);
    Assert.That(grown.Length, Is.GreaterThan(first.Length));
    var large = buffer.Get(SerializationBuffer.DefaultMaxKeptLength + 1);
    Assert.That(large.Length, Is.EqualTo(SerializationBuffer.DefaultMaxKeptLength + 1));
    Assert.That(buffer.Get(10) == grown, Is.True, "the longer entry did not replace the buffer");
  }

  [Test]
  public void ABufferWithoutAKeptLengthKeepsLongEntriesAtTheirLength()
  {
    var buffer = new SerializationBuffer(int.MaxValue);
    var length = 3 * SerializationBuffer.DefaultMaxKeptLength + 5;
    var kept = buffer.Get(length);
    Assert.That(kept.Length, Is.EqualTo(length), "a long buffer takes the length asked for, without doubling");
    Assert.That(buffer.Get(10) == kept, Is.True, "the long buffer is kept");
    var longer = buffer.Get(length + 1);
    Assert.That(longer.Length, Is.EqualTo(length + 1));
    Assert.That(buffer.Get(length) == longer, Is.True, "the longer buffer replaced it");
  }

  [Test]
  public void RecordsHoldTheKeyAndValueBytesBetweenTheHeaderAndTheTrailer()
  {
    foreach (var value in new[] { null, "", "ğüş😀", new string('b', 380), new string('x', 100_000) })
    {
      var record = WalEntrySerializer.SerializeRecord(
          Int64, Utf8, -7L, value, 16, 4, out var keyLength, out var valueLength).ToArray();
      var key = Int64.Serialize(-7L).ToArray();
      var expectedValue = Utf8.Serialize(value).ToArray();
      Assert.That(keyLength, Is.EqualTo(key.Length));
      Assert.That(valueLength, Is.EqualTo(expectedValue.Length));
      Assert.That(record.Length, Is.EqualTo(16 + key.Length + expectedValue.Length + 4));
      Assert.That(record.AsSpan(16, keyLength).ToArray(), Is.EqualTo(key));
      Assert.That(record.AsSpan(16 + keyLength, valueLength).ToArray(), Is.EqualTo(expectedValue));
    }
  }

  [Test]
  public void RecordsUpTo64KBReuseTheThreadBuffer()
  {
    var value = new string('b', 380);
    // The thread's first record of this size creates the buffer.
    WalEntrySerializer.SerializeRecord(Int64, Utf8, 1L, value, 16, 4, out _, out _);
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0L; i < 1000; ++i)
      WalEntrySerializer.SerializeRecord(Int64, Utf8, i, value, 16, 4, out _, out _);
    Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
  }
}
