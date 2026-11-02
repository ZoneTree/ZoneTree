using System.Diagnostics;
using System.Numerics;

namespace Transactions.Benchmark.Measurement;

/// <summary>
/// A latency histogram with log-spaced buckets: 16 per power of two, so a percentile
/// is within about 6% of the measured value. Each worker owns one, and the run merges them.
/// </summary>
public sealed class LatencyHistogram
{
  const int SubBucketBits = 4;
  const int SubBucketCount = 1 << SubBucketBits;
  const int MagnitudeCount = 64;

  readonly long[] Buckets = new long[MagnitudeCount * SubBucketCount];
  long Count;
  long TotalTicks;
  long MaxTicks;

  public void Record(long ticks)
  {
    ticks = Math.Max(ticks, 1);
    ++Buckets[BucketOf(ticks)];
    ++Count;
    TotalTicks += ticks;
    if (ticks > MaxTicks)
      MaxTicks = ticks;
  }

  public void Merge(LatencyHistogram other)
  {
    for (var i = 0; i < Buckets.Length; i++)
      Buckets[i] += other.Buckets[i];
    Count += other.Count;
    TotalTicks += other.TotalTicks;
    MaxTicks = Math.Max(MaxTicks, other.MaxTicks);
  }

  public LatencySummary Summarize() =>
      Count == 0
          ? new LatencySummary(0, 0, 0, 0, 0, 0)
          : new LatencySummary(
              ToMicroseconds(TotalTicks / (double)Count),
              Percentile(0.50),
              Percentile(0.95),
              Percentile(0.99),
              Percentile(0.999),
              ToMicroseconds(MaxTicks));

  double Percentile(double fraction)
  {
    var target = (long)Math.Ceiling(Count * fraction);
    var seen = 0L;
    for (var i = 0; i < Buckets.Length; i++)
    {
      seen += Buckets[i];
      if (seen >= target)
        return Math.Min(ToMicroseconds(MidpointOf(i)), ToMicroseconds(MaxTicks));
    }
    return ToMicroseconds(MaxTicks);
  }

  static int BucketOf(long ticks)
  {
    var magnitude = BitOperations.Log2((ulong)ticks);
    if (magnitude < SubBucketBits)
      return (int)ticks;
    var subBucket = (int)((ticks >> (magnitude - SubBucketBits)) & (SubBucketCount - 1));
    return (magnitude - SubBucketBits + 1) * SubBucketCount + subBucket;
  }

  static double MidpointOf(int bucket)
  {
    if (bucket < SubBucketCount)
      return bucket;
    var magnitude = bucket / SubBucketCount - 1 + SubBucketBits;
    var subBucket = bucket % SubBucketCount;
    var width = 1L << (magnitude - SubBucketBits);
    return ((1L << magnitude) + subBucket * width) + width / 2.0;
  }

  static double ToMicroseconds(double ticks) =>
      ticks * 1_000_000.0 / Stopwatch.Frequency;
}

public sealed record LatencySummary(
    double MeanUs,
    double P50Us,
    double P95Us,
    double P99Us,
    double P999Us,
    double MaxUs);
