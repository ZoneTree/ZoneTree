#if NET11_0_OR_GREATER
using System.Buffers;
using System.IO.Compression;
#else
using ZstdSharp;
#endif

namespace ZoneTree.Compression;

public static class ZstdDataCompression
{
#if NET11_0_OR_GREATER
  [ThreadStatic]
  static (int Level, ZstandardEncoder Encoder) ThreadEncoder;

  [ThreadStatic]
  static ZstandardDecoder ThreadDecoder;

  public static Memory<byte> Compress(Memory<byte> bytes, int level)
  {
    var encoder = GetEncoder(level);
    var compressed = new byte[ZstandardEncoder.GetMaxCompressedLength(bytes.Length)];
    if (encoder.Compress(
        bytes.Span,
        compressed.AsSpan(),
        out _,
        out var compressedLength,
        true) != OperationStatus.Done)
    {
      throw new InvalidDataException("Zstandard compression failed.");
    }
    encoder.Reset();
    return compressed.AsMemory(0, compressedLength);
  }

  public static byte[] Decompress(Memory<byte> compressedBytes, int decompressedLength)
  {
    if (decompressedLength == 0)
    {
      return Array.Empty<byte>();
    }

    var decompressed = new byte[decompressedLength];
    var decoder = GetDecoder();
    decoder.Reset();
    if (decoder.Decompress(
        compressedBytes.Span,
        decompressed.AsSpan(),
        out _,
        out var bytesWritten) != OperationStatus.Done ||
        bytesWritten != decompressedLength)
    {
      throw new InvalidDataException("Zstandard decompression failed.");
    }
    return decompressed;
  }

  static ZstandardEncoder GetEncoder(int level)
{
    ref var cached = ref ThreadEncoder;

    if (cached.Encoder != null && cached.Level == level)
        return cached.Encoder;

    var encoder = new ZstandardEncoder(level);
    cached.Encoder?.Dispose();
    cached = (level, encoder);

    return encoder;
}

  static ZstandardDecoder GetDecoder()
  {
    var decoder = ThreadDecoder;
    if (decoder == null)
    {
      decoder = new ZstandardDecoder();
      ThreadDecoder = decoder;
    }
    return decoder;
  }
#else
  [ThreadStatic]
  static Compressor ThreadCompressor;

  [ThreadStatic]
  static Decompressor ThreadDecompressor;

  public static Memory<byte> Compress(Memory<byte> bytes, int level)
  {
    var compressor = GetCompressor(level);
    var compressed = new byte[Compressor.GetCompressBound(bytes.Length)];
    var compressedLength = compressor.Wrap(bytes.Span, compressed.AsSpan());
    return compressed.AsMemory(0, compressedLength);
  }

  public static Memory<byte> CompressInto(Memory<byte> bytes, int level, ref byte[] buffer)
  {
    var bound = Compressor.GetCompressBound(bytes.Length);
    if (buffer == null || buffer.Length < bound)
      buffer = new byte[bound];
    var compressedLength = GetCompressor(level).Wrap(bytes.Span, buffer.AsSpan());
    return buffer.AsMemory(0, compressedLength);
  }

  public static byte[] Decompress(Memory<byte> compressedBytes, int decompressedLength)
  {
    var decompressed = new byte[decompressedLength];
    var decompressor = GetDecompressor();
    decompressor.Unwrap(compressedBytes.Span, decompressed.AsSpan());
    return decompressed;
  }

  public static void DecompressInto(Memory<byte> compressedBytes, Memory<byte> destination)
  {
    // Destinations can be reused buffers: a short result would leave stale bytes.
    if (GetDecompressor().Unwrap(compressedBytes.Span, destination.Span) != destination.Length)
      throw new InvalidDataException("Zstd decompression failed.");
  }

  static Compressor GetCompressor(int level)
  {
    var compressor = ThreadCompressor;
    if (compressor == null)
    {
      compressor = new Compressor(level);
      ThreadCompressor = compressor;
      return compressor;
    }
    compressor.Level = level;
    return compressor;
  }

  static Decompressor GetDecompressor()
  {
    var decompressor = ThreadDecompressor;
    if (decompressor == null)
    {
      decompressor = new Decompressor();
      ThreadDecompressor = decompressor;
    }
    return decompressor;
  }
#endif
}
