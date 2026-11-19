using System.Text;

namespace ZoneTree.Serializers;

public sealed class Utf8StringSerializer : ISerializer<string>
{
  // Single byte 0xC2 is not a valid UTF-8 string.
  // We can use that to serialize the null strings.
  const byte NullMarker = 0xC2;

  // Up to this length, the maximum is the bound of three bytes per char, which needs no pass over the string. A
  // longer string is counted, so a buffer for it is its size instead of three times its size.
  const int BoundedLength = 16 * 1024;

  public string Deserialize(Memory<byte> bytes)
  {
    if (bytes.Length == 1 && bytes.Span[0] == 0xC2)
      return null;
    return Encoding.UTF8.GetString(bytes.Span);
  }

  public Memory<byte> Serialize(in string entry)
  {
    if (entry == null)
    {
      return new byte[1] { NullMarker };
    }
    return Encoding.UTF8.GetBytes(entry);
  }

  public int GetMaxByteCount(in string entry)
  {
    if (entry == null)
      return 1;
    return entry.Length <= BoundedLength
        ? Encoding.UTF8.GetMaxByteCount(entry.Length)
        : Encoding.UTF8.GetByteCount(entry);
  }

  public int Serialize(in string entry, Span<byte> destination)
  {
    if (entry == null)
    {
      destination[0] = NullMarker;
      return 1;
    }
    return Encoding.UTF8.GetBytes(entry, destination);
  }
}
