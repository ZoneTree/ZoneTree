using System.IO.Compression;
using System.Runtime.InteropServices;
using ZoneTree.AbstractFileStream;

namespace ZoneTree.Compression;

public static class GZipDataCompression
{
  public static Memory<byte> Compress(Memory<byte> bytes, int level)
  {
    using var msOutput = new MemoryStream(bytes.Length);
    using (var gzs = new GZipStream(msOutput, (CompressionLevel)level, true))
    {
      gzs.Write(bytes.Span);
    }
    return msOutput.ToArray();
  }

  public static byte[] Decompress(Memory<byte> compressedBytes, int decompressedLength)
  {
    var decompressed = new byte[decompressedLength];
    using var pin = compressedBytes.Pin();
    using var msInput = compressedBytes.ToReadOnlyStream(pin);
    using var msOutput = new MemoryStream(decompressed, true);
    using var gzs = new GZipStream(msInput, CompressionMode.Decompress);
    gzs.CopyTo(msOutput);
    return decompressed;
  }

  public static void DecompressInto(Memory<byte> compressedBytes, Memory<byte> destination)
  {
    if (!MemoryMarshal.TryGetArray<byte>(destination, out var segment))
    {
      Decompress(compressedBytes, destination.Length).AsSpan().CopyTo(destination.Span);
      return;
    }
    using var pin = compressedBytes.Pin();
    using var msInput = compressedBytes.ToReadOnlyStream(pin);
    using var msOutput = new MemoryStream(segment.Array, segment.Offset, segment.Count, true);
    using var gzs = new GZipStream(msInput, CompressionMode.Decompress);
    gzs.CopyTo(msOutput);
    // Destinations can be reused buffers: a short result would leave stale bytes.
    if (msOutput.Position != segment.Count)
      throw new InvalidDataException("Gzip decompression failed.");
  }
}
