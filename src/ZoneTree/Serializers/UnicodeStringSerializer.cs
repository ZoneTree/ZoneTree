using System.Text;

namespace ZoneTree.Serializers;

public sealed class UnicodeStringSerializer : ISerializer<string>
{
  public string Deserialize(Memory<byte> bytes)
  {
    if (bytes.Length == 1)
      return null;
    return Encoding.Unicode.GetString(bytes.Span);
  }

  public static string Deserialize(Span<byte> bytes)
  {
    if (bytes.Length == 1)
      return null;
    return Encoding.Unicode.GetString(bytes);
  }

  public Memory<byte> Serialize(in string entry)
  {
    if (entry == null)
      return new byte[1] { 0 };
    return Encoding.Unicode.GetBytes(entry);
  }

  public int GetMaxByteCount(in string entry)
      => entry == null ? 1 : Encoding.Unicode.GetMaxByteCount(entry.Length);

  public int Serialize(in string entry, Span<byte> destination)
  {
    if (entry == null)
    {
      destination[0] = 0;
      return 1;
    }
    return Encoding.Unicode.GetBytes(entry, destination);
  }
}
