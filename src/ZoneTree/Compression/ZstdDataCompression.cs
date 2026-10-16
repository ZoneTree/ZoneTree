#if NET11_0_OR_GREATER
using System.IO.Compression;
#else
using ZstdSharp;
#endif

namespace ZoneTree.Compression;

public static class ZstdDataCompression
{
#if NET11_0_OR_GREATER
  public static Memory<byte> Compress(Memory<byte> bytes, int level)
  {
    var compressed = new byte[ZstandardEncoder.GetMaxCompressedLength(bytes.Length)];
    if (!ZstandardEncoder.TryCompress(
        bytes.Span,
        compressed.AsSpan(),
        out var compressedLength,
        level,
        0))
    {
      throw new InvalidDataException("Zstandard compression failed.");
    }
    return compressed.AsMemory(0, compressedLength);
  }

  public static byte[] Decompress(Memory<byte> compressedBytes, int decompressedLength)
  {
    var decompressed = new byte[decompressedLength];
    if (!ZstandardDecoder.TryDecompress(
        compressedBytes.Span,
        decompressed.AsSpan(),
        out var bytesWritten) ||
        bytesWritten != decompressedLength)
    {
      throw new InvalidDataException("Zstandard decompression failed.");
    }
    return decompressed;
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

  public static byte[] Decompress(Memory<byte> compressedBytes, int decompressedLength)
  {
    var decompressed = new byte[decompressedLength];
    var decompressor = GetDecompressor();
    decompressor.Unwrap(compressedBytes.Span, decompressed.AsSpan());
    return decompressed;
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
