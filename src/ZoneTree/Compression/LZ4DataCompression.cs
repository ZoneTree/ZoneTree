using K4os.Compression.LZ4;

namespace ZoneTree.Compression;

public static class LZ4DataCompression
{
  public static Memory<byte> Compress(Memory<byte> bytes, int level)
  {
    return LZ4Pickler.Pickle(bytes.Span, (LZ4Level)level);
  }

  public static byte[] Decompress(Memory<byte> compressedBytes, int decompressedLength)
  {
    var bytes = new byte[decompressedLength];
    LZ4Pickler.Unpickle(compressedBytes.Span, bytes);
    return bytes;
  }

  public static void DecompressInto(Memory<byte> compressedBytes, Memory<byte> destination)
  {
    // Destinations can be reused buffers: a short result would leave stale bytes.
    if (LZ4Pickler.UnpickledSize(compressedBytes.Span) != destination.Length)
      throw new InvalidDataException("LZ4 decompression failed.");
    LZ4Pickler.Unpickle(compressedBytes.Span, destination.Span);
  }

  public static int GetDecompressedLength(Memory<byte> compressedBytes)
  {
    return checked(LZ4Pickler.UnpickledSize(compressedBytes.Span));
  }
}
