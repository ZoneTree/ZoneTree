using ZstdSharp;

namespace ZoneTree.Compression;

public static class ZstdDataCompression
{
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
}
