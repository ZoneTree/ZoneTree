using System.Numerics;

namespace ZoneTree.Serializers;

/// <summary>Serializes arbitrary-precision integers as their two's complement bytes, least significant first.</summary>
public sealed class BigIntegerSerializer : ISerializer<BigInteger>
{
  public BigInteger Deserialize(Memory<byte> bytes)
    => new(bytes.Span);

  public Memory<byte> Serialize(in BigInteger entry)
    => entry.ToByteArray();

  public int GetMaxByteCount(in BigInteger entry) => entry.GetByteCount();

  /// <exception cref="ArgumentException">The destination is shorter than the integer's bytes.</exception>
  public int Serialize(in BigInteger entry, Span<byte> destination)
  {
    if (!entry.TryWriteBytes(destination, out var written))
      throw new ArgumentException("The destination is shorter than the integer's bytes.", nameof(destination));
    return written;
  }
}
