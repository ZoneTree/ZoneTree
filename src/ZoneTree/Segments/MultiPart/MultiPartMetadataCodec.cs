using System.Buffers.Binary;
using ZoneTree.Compression;
using ZoneTree.Options;

namespace ZoneTree.Segments.MultiPart;

internal static class MultiPartMetadataCodec
{
  internal const CompressionMethod CurrentCompressionMethod = CompressionMethod.Zstd;

  internal const int CurrentCompressionLevel = CompressionLevels.Zstd1;

  const int MagicSize = 16;

  const byte Version = 1;

  const int PrefixSize =
      MagicSize +
      sizeof(byte) +
      sizeof(byte) +
      sizeof(int) +
      sizeof(int) +
      sizeof(int);

  static ReadOnlySpan<byte> Magic => "ZoneTreeMPH-v001"u8;

  readonly record struct Envelope(
      CompressionMethod Method,
      Memory<byte> Payload,
      int DecompressedLength);

  public static Memory<byte> Encode(Memory<byte> bytes)
  {
    var payload = DataCompression.Compress(
        CurrentCompressionMethod,
        CurrentCompressionLevel,
        bytes);

    var result = new byte[PrefixSize + payload.Length];
    var span = result.AsSpan();
    Magic.CopyTo(span);

    var offset = MagicSize;
    span[offset++] = Version;
    span[offset++] = (byte)CurrentCompressionMethod;
    BinaryPrimitives.WriteInt32LittleEndian(
        span.Slice(offset, sizeof(int)),
        CurrentCompressionLevel);
    offset += sizeof(int);
    BinaryPrimitives.WriteInt32LittleEndian(
        span.Slice(offset, sizeof(int)),
        payload.Length);
    offset += sizeof(int);
    BinaryPrimitives.WriteInt32LittleEndian(
        span.Slice(offset, sizeof(int)),
        bytes.Length);
    offset += sizeof(int);
    payload.Span.CopyTo(span[offset..]);
    return result;
  }

  public static Memory<byte> Decode(Memory<byte> bytes)
  {
    if (!HasMagic(bytes))
      throw new InvalidDataException("Multipart metadata is damaged: its magic is invalid.");

    var envelope = ReadEnvelope(bytes);
    return DataCompression.Decompress(
        envelope.Method,
        envelope.Payload,
        envelope.DecompressedLength);
  }

  static bool HasMagic(Memory<byte> bytes)
  {
    if (bytes.Length < MagicSize)
      return false;

    return bytes.Span[..MagicSize].SequenceEqual(Magic);
  }

  static Envelope ReadEnvelope(Memory<byte> bytes)
  {
    if (bytes.Length < PrefixSize)
      throw new InvalidDataException("Multipart metadata envelope is incomplete.");

    var span = bytes.Span;
    var offset = MagicSize;
    var version = span[offset++];
    if (version != Version)
      throw new InvalidDataException(
          $"Unsupported multipart metadata envelope version: {version}.");

    var method = (CompressionMethod)span[offset++];
    var level = BinaryPrimitives.ReadInt32LittleEndian(
        span.Slice(offset, sizeof(int)));
    offset += sizeof(int);
    if (!CompressionLevels.IsValid(method, level))
      throw new InvalidDataException(
          $"Invalid multipart metadata compression profile: {method}, level {level}.");

    var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
        span.Slice(offset, sizeof(int)));
    offset += sizeof(int);
    var decompressedLength = BinaryPrimitives.ReadInt32LittleEndian(
        span.Slice(offset, sizeof(int)));
    offset += sizeof(int);
    if (payloadLength < 0 || payloadLength != span.Length - offset)
      throw new InvalidDataException(
          "Multipart metadata envelope payload length is invalid.");
    if (decompressedLength < 0)
      throw new InvalidDataException(
          "Multipart metadata envelope decompressed length is invalid.");

    return new Envelope(
        method,
        bytes.Slice(offset, payloadLength),
        decompressedLength);
  }
}
