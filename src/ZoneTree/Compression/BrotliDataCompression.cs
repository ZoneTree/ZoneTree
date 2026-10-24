using System.IO.Compression;

namespace ZoneTree.Compression;

public static class BrotliDataCompression
{
  const int DefaultWindow = 22;

  public static Memory<byte> Compress(Memory<byte> bytes, int level)
  {
    var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(bytes.Length)];
    if (!BrotliEncoder.TryCompress(
        bytes.Span,
        compressed.AsSpan(),
        out var compressedLength,
        GetQuality(level),
        DefaultWindow))
    {
      throw new InvalidDataException("Brotli compression failed.");
    }
    return compressed.AsMemory(0, compressedLength);
  }

  public static Memory<byte> CompressInto(Memory<byte> bytes, int level, ref byte[] buffer)
  {
    var bound = BrotliEncoder.GetMaxCompressedLength(bytes.Length);
    if (buffer == null || buffer.Length < bound)
      buffer = new byte[bound];
    if (!BrotliEncoder.TryCompress(
        bytes.Span,
        buffer.AsSpan(),
        out var compressedLength,
        GetQuality(level),
        DefaultWindow))
    {
      throw new InvalidDataException("Brotli compression failed.");
    }
    return buffer.AsMemory(0, compressedLength);
  }

  public static byte[] Decompress(Memory<byte> compressedBytes, int decompressedLength)
  {
    var decompressed = new byte[decompressedLength];
    if (!BrotliDecoder.TryDecompress(
        compressedBytes.Span,
        decompressed.AsSpan(),
        out var bytesWritten) ||
        bytesWritten != decompressedLength)
    {
      throw new InvalidDataException("Brotli decompression failed.");
    }
    return decompressed;
  }

  public static void DecompressInto(Memory<byte> compressedBytes, Memory<byte> destination)
  {
    if (!BrotliDecoder.TryDecompress(
        compressedBytes.Span,
        destination.Span,
        out var bytesWritten) ||
        bytesWritten != destination.Length)
    {
      throw new InvalidDataException("Brotli decompression failed.");
    }
  }

  static int GetQuality(int level)
  {
    return (CompressionLevel)level switch
    {
      CompressionLevel.NoCompression => 0,
      CompressionLevel.SmallestSize => 11,
      // Brotli Optimal is extremely slow.
      // treat Optimal like Fastest.
      _ => 1,
    };
  }
}
