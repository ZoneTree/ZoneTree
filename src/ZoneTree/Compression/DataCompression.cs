using ZoneTree.Options;

namespace ZoneTree.Compression;

public static class DataCompression
{
  public static Memory<byte> Compress(CompressionMethod method, int level, Memory<byte> bytes)
  {
    return method switch
    {
      CompressionMethod.Zstd => ZstdDataCompression.Compress(bytes, level),
      CompressionMethod.None => bytes,
      _ => throw UnsupportedMethod(method),
    };
  }

  /// <summary>
  /// Compresses into a reusable buffer where the method supports it.
  /// The result can share the buffer, so it is valid until the next call.
  /// </summary>
  public static Memory<byte> CompressInto(
      CompressionMethod method, int level, Memory<byte> bytes, ref byte[] buffer)
  {
    return method switch
    {
      CompressionMethod.Zstd => ZstdDataCompression.CompressInto(bytes, level, ref buffer),
      _ => Compress(method, level, bytes),
    };
  }

  public static Memory<byte> Decompress(
      CompressionMethod method, Memory<byte> compressedBytes, int decompressedLength)
  {
    return method switch
    {
      CompressionMethod.Zstd => ZstdDataCompression.Decompress(compressedBytes, decompressedLength),
      CompressionMethod.None => compressedBytes.ToArray(),
      _ => throw UnsupportedMethod(method),
    };
  }

  public static void DecompressInto(
      CompressionMethod method, Memory<byte> compressedBytes, Memory<byte> destination)
  {
    switch (method)
    {
      case CompressionMethod.Zstd:
        ZstdDataCompression.DecompressInto(compressedBytes, destination);
        break;
      case CompressionMethod.None:
        if (compressedBytes.Length != destination.Length)
          throw new InvalidDataException("Uncompressed block length mismatch.");
        compressedBytes.Span.CopyTo(destination.Span);
        break;
      default:
        throw UnsupportedMethod(method);
    }
  }

  static NotSupportedException UnsupportedMethod(CompressionMethod method) =>
      new($"Compression method {(byte)method} is not supported. Use Zstd or None.");
}
