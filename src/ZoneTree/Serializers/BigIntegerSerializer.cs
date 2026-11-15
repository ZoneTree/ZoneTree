using System.Numerics;

namespace ZoneTree.Serializers;

/// <summary>Serializes arbitrary-precision integers as their two's complement bytes, least significant first.</summary>
public sealed class BigIntegerSerializer : ISerializer<BigInteger>
{
  public BigInteger Deserialize(Memory<byte> bytes)
    => new(bytes.Span);

  public Memory<byte> Serialize(in BigInteger entry)
    => entry.ToByteArray();
}
