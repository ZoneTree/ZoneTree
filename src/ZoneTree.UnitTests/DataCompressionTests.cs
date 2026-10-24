using System.Text;
using ZoneTree.Compression;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

/// <summary>
/// Codec-level round-trip tests for <see cref="DataCompression"/>.
///
/// These cover the two gaps that let a decoder-reuse regression slip through:
///   * the empty (zero-length) block, which the net11 instance decoder
///     handles differently from the static one-shot path, and
///   * repeated reuse of the thread-static encoder/decoder across many
///     operations with varying sizes and levels.
/// </summary>
public sealed class DataCompressionTests
{
  static readonly CompressionMethod[] Methods =
  {
    CompressionMethod.Zstd,
    CompressionMethod.LZ4,
    CompressionMethod.Brotli,
    CompressionMethod.Gzip,
    CompressionMethod.None,
  };

  static int DefaultLevel(CompressionMethod method) => method switch
  {
    CompressionMethod.Zstd => CompressionLevels.Zstd0,
    CompressionMethod.LZ4 => CompressionLevels.LZ4Fastest,
    CompressionMethod.Brotli => CompressionLevels.BrotliFastest,
    CompressionMethod.Gzip => CompressionLevels.GzipFastest,
    _ => 0,
  };

  static byte[] BuildPayload(int length, int seed)
  {
    var sb = new StringBuilder(length + 64);
    int id = 0;
    while (sb.Length < length)
      sb.Append("{\"id\":").Append(id++ + seed)
        .Append(",\"country\":\"US\",\"status\":\"active\"}");
    return Encoding.UTF8.GetBytes(sb.ToString(0, length));
  }

  [Test]
  public void RoundTrips_ForEveryMethodAndLevel(
    [Values] CompressionMethod method,
    [Values(0, 1, 1024, 4096, 65536)] int length)
  {
    var level = DefaultLevel(method);
    var original = BuildPayload(length, 0);

    var compressed = DataCompression.Compress(method, level, original);
    var decompressed = DataCompression.Decompress(method, compressed, length);

    Assert.That(decompressed.Length, Is.EqualTo(length));
    Assert.That(decompressed.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void RoundTrips_EmptyBlock_ForEveryMethod([Values] CompressionMethod method)
  {
    var level = DefaultLevel(method);
    var original = Array.Empty<byte>();

    // Regression guard: the net11 instance decoder returned DestinationTooSmall
    // for a zero-length destination, throwing before this guard existed.
    var compressed = DataCompression.Compress(method, level, original);
    var decompressed = DataCompression.Decompress(method, compressed, 0);

    Assert.That(decompressed.Length, Is.EqualTo(0));
  }

  [Test]
  public void Zstd_RoundTrips_AcrossManyReusedOperations()
  {
    // Exercises the thread-static encoder/decoder reuse with varying sizes so a
    // stale decoder state or an off-by-one in a reused buffer cannot hide.
    for (int i = 0; i < 500; i++)
    {
      int length = (i * 977) % 8192;
      var original = BuildPayload(length, i);

      var compressed = DataCompression.Compress(CompressionMethod.Zstd, CompressionLevels.Zstd0, original);
      var decompressed = DataCompression.Decompress(CompressionMethod.Zstd, compressed, length);

      Assert.That(decompressed.ToArray(), Is.EqualTo(original), $"mismatch at iteration {i}, length {length}");
    }
  }

  [Test]
  public void Zstd_RoundTrips_AcrossLevels_OnSharedThread()
  {
    // The production encoder is re-keyed by level on a shared thread-static slot.
    var levels = new[]
    {
      CompressionLevels.Zstd0,
      CompressionLevels.Zstd1,
      CompressionLevels.Zstd3,
      CompressionLevels.Zstd5,
    };

    foreach (var level in levels)
    {
      var original = BuildPayload(4096, level);
      var compressed = DataCompression.Compress(CompressionMethod.Zstd, level, original);
      var decompressed = DataCompression.Decompress(CompressionMethod.Zstd, compressed, original.Length);
      Assert.That(decompressed.ToArray(), Is.EqualTo(original), $"mismatch at level {level}");
    }
  }

  [Test]
  public void Zstd_RoundTrips_UnderParallelLoad()
  {
    // The thread-static decoder must be safe across concurrent threads, matching
    // the Parallel.For ingestion path in the WAL/disks segments.
    var payloads = Enumerable.Range(0, 200).Select(i => BuildPayload((i * 331) % 6000, i)).ToArray();

    Parallel.For(0, payloads.Length, i =>
    {
      var original = payloads[i];
      var compressed = DataCompression.Compress(CompressionMethod.Zstd, CompressionLevels.Zstd0, original);
      var decompressed = DataCompression.Decompress(CompressionMethod.Zstd, compressed, original.Length);
      Assert.That(decompressed.ToArray(), Is.EqualTo(original), $"parallel mismatch at {i}, length {original.Length}");
    });
  }
}
