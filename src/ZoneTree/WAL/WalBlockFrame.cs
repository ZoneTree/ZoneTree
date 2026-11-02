using System.Buffers.Binary;
using System.IO.Hashing;

namespace ZoneTree.WAL;

/// <summary>
/// A self-validating main-file block. The metadata flag distinguishes this
/// layout from the original three-integer block header. Header validation
/// precedes allocation; the payload checksum covers the stored compressed bytes.
/// </summary>
internal static class WalBlockFrame
{
  internal const byte MetadataFlag = 0x80;

  internal const int HeaderSize = 32;

  const ulong Magic = 0x324B4C424C41575A; // ZWALBLK2

  // The WAL options permit blocks up to 16 MiB. Allow codec overhead without
  // trusting lengths from an unvalidated header or the reopened block size.
  const int MaximumBlockLength = 16 * 1024 * 1024;

  internal static bool HasMagic(ReadOnlySpan<byte> header) =>
      header.Length >= sizeof(ulong) && BinaryPrimitives.ReadUInt64LittleEndian(header) == Magic;

  internal static void Write(Span<byte> header, int index, int length, ReadOnlySpan<byte> payload)
  {
    if (!ValidLengths(index, payload.Length, length))
      throw new InvalidDataException("The WAL block exceeds the supported format limits.");
    BinaryPrimitives.WriteUInt64LittleEndian(header, Magic);
    BinaryPrimitives.WriteInt32LittleEndian(header[8..], index);
    BinaryPrimitives.WriteInt32LittleEndian(header[12..], payload.Length);
    BinaryPrimitives.WriteInt32LittleEndian(header[16..], length);
    BinaryPrimitives.WriteUInt64LittleEndian(header[20..], XxHash3.HashToUInt64(payload));
    BinaryPrimitives.WriteUInt32LittleEndian(header[28..],
        unchecked((uint)XxHash3.HashToUInt64(header[..28])));
  }

  internal static bool TryRead(ReadOnlySpan<byte> header,
      out int index, out int compressedLength, out int length, out ulong checksum)
  {
    index = compressedLength = length = 0;
    checksum = 0;
    if (header.Length != HeaderSize ||
        BinaryPrimitives.ReadUInt64LittleEndian(header) != Magic ||
        BinaryPrimitives.ReadUInt32LittleEndian(header[28..]) !=
            unchecked((uint)XxHash3.HashToUInt64(header[..28])))
      return false;
    index = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
    compressedLength = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
    length = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
    checksum = BinaryPrimitives.ReadUInt64LittleEndian(header[20..]);
    return ValidLengths(index, compressedLength, length);
  }

  static bool ValidLengths(int index, int compressedLength, int length) =>
      index >= 0 && length > 0 && length <= MaximumBlockLength &&
      compressedLength > 0 && compressedLength <= 2 * MaximumBlockLength + 65536;
}
