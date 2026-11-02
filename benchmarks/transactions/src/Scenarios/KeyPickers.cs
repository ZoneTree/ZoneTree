namespace Transactions.Benchmark.Scenarios;

/// <summary>
/// Picks one of a fixed number of keys.
/// </summary>
public delegate long KeyPicker(Random random);

public static class KeyPickers
{
  public static KeyPicker Uniform(long keys) =>
      random => random.NextInt64(keys);

  public static KeyPicker Zipf(long keys, double theta)
  {
    var zipf = new ScrambledZipf(keys, theta);
    return zipf.Next;
  }
}

/// <summary>
/// The scrambled zipfian generator of YCSB: a few keys are hot, and they are spread over
/// the key space instead of sitting next to each other.
/// </summary>
sealed class ScrambledZipf
{
  const ulong FnvOffset = 14695981039346656037;
  const ulong FnvPrime = 1099511628211;

  readonly long Items;
  readonly double Theta;
  readonly double Zeta;
  readonly double Alpha;
  readonly double Eta;

  public ScrambledZipf(long items, double theta)
  {
    Items = items;
    Theta = theta;
    Zeta = ComputeZeta(items, theta);
    Alpha = 1.0 / (1.0 - theta);
    Eta = (1.0 - Math.Pow(2.0 / items, 1.0 - theta)) / (1.0 - ComputeZeta(2, theta) / Zeta);
  }

  public long Next(Random random)
  {
    var u = random.NextDouble();
    var uz = u * Zeta;
    long rank;
    if (uz < 1.0)
      rank = 0;
    else if (uz < 1.0 + Math.Pow(0.5, Theta))
      rank = 1;
    else
      rank = (long)(Items * Math.Pow(Eta * u - Eta + 1.0, Alpha));
    return (long)(Fnv(rank) % (ulong)Items);
  }

  static double ComputeZeta(long items, double theta)
  {
    var sum = 0.0;
    for (var i = 1L; i <= items; i++)
      sum += 1.0 / Math.Pow(i, theta);
    return sum;
  }

  static ulong Fnv(long value)
  {
    var hash = FnvOffset;
    for (var i = 0; i < sizeof(long); i++)
    {
      hash ^= (ulong)(value & 0xff);
      hash *= FnvPrime;
      value >>= 8;
    }
    return hash;
  }
}
