using System.Globalization;
using ZoneTree.Options;

namespace ZoneTree.WalSoak;

sealed record Options
{
  public string Root { get; init; } = Path.GetFullPath(".tmp/wal-soak");
  public int Seed { get; init; } = 173219;
  public int Workers { get; init; } = 8;
  public int Readers { get; init; } = 2;
  public int Accounts { get; init; } = 128;
  public int Slots { get; init; } = 128;
  public int EpochSeconds { get; init; } = 300;
  public int LongEpochSeconds { get; init; } = 21600;
  public int LongEvery { get; init; } = 48;
  public int LongRate { get; init; } = 25;
  public int DurationSeconds { get; init; } = 86400;
  public int Cycles { get; init; }
  public int Retain { get; init; } = 3;
  public int ProgressSeconds { get; init; } = 10;
  public int StallSeconds { get; init; } = 120;
  public int RecoveryTimeoutSeconds { get; init; } = 180;
  public int MaxRssMiB { get; init; } = 8192;
  public int MaxDiskGiB { get; init; } = 60;
  public int MinFreeGiB { get; init; } = 15;
  public int MaxSequence { get; init; } = 1_000_000;
  public int MaxOutstanding { get; init; } = 4096;
  public int SnapshotSeconds { get; init; } = 15;
  public bool Resume { get; init; }
  public bool SelfTest { get; init; }

  public static Options Parse(string[] args)
  {
    var result = new Options { SelfTest = args.FirstOrDefault() == "self-test" };
    var start = args.FirstOrDefault() is "run" or "self-test" ? 1 : 0;
    for (var i = start; i < args.Length; ++i)
    {
      var name = args[i];
      if (name == "--resume") { result = result with { Resume = true }; continue; }
      if (++i == args.Length) throw new ArgumentException($"Missing value for {name}.");
      var value = args[i];
      int Number(int minimum, int maximum)
      {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum)
          throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");
        return number;
      }
      result = name switch
      {
        "--root" => result with { Root = Path.GetFullPath(value) },
        "--seed" => result with { Seed = Number(0, int.MaxValue) },
        "--workers" => result with { Workers = Number(1, 64) },
        "--readers" => result with { Readers = Number(0, 8) },
        "--accounts" => result with { Accounts = Number(8, 4096) },
        "--slots" => result with { Slots = Number(8, 4096) },
        "--epoch" => result with { EpochSeconds = Duration(value) },
        "--long-epoch" => result with { LongEpochSeconds = Duration(value) },
        "--long-every" => result with { LongEvery = Number(0, 10000) },
        "--long-rate" => result with { LongRate = Number(1, 100000) },
        "--duration" => result with { DurationSeconds = Duration(value) },
        "--cycles" => result with { Cycles = Number(1, 1_000_000) },
        "--retain" => result with { Retain = Number(0, 100) },
        "--progress" => result with { ProgressSeconds = Duration(value) },
        "--stall" => result with { StallSeconds = Duration(value) },
        "--recovery-timeout" => result with { RecoveryTimeoutSeconds = Duration(value) },
        "--max-rss-mb" => result with { MaxRssMiB = Number(256, 1_000_000) },
        "--max-disk-gb" => result with { MaxDiskGiB = Number(1, 1_000_000) },
        "--min-free-gb" => result with { MinFreeGiB = Number(0, 1_000_000) },
        "--max-sequence" => result with { MaxSequence = Number(100, 10_000_000) },
        "--max-outstanding" => result with { MaxOutstanding = Number(1, 1000000) },
        "--snapshot" => result with { SnapshotSeconds = Duration(value) },
        _ => throw new ArgumentException($"Unknown option {name}. See --help.")
      };
    }
    if (result.EpochSeconds < 2) throw new ArgumentException("--epoch must be at least 2s.");
    if (result.SelfTest)
      result = result with { Workers = 3, Readers = 1, Accounts = 16, Slots = 16, EpochSeconds = 4, LongEvery = 0, SnapshotSeconds = 1, MinFreeGiB = 0, Cycles = 4 };
    return result;
  }

  static int Duration(string text)
  {
    var scale = text.LastOrDefault() switch
    {
      's' => 1, 'm' => 60, 'h' => 3600, 'd' => 86400,
      var c when char.IsDigit(c) => 1,
      _ => throw new ArgumentException($"Invalid duration '{text}'. Use seconds or s/m/h/d suffixes.")
    };
    var number = char.IsLetter(text.LastOrDefault()) ? text[..^1] : text;
    if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) || n <= 0 || n * scale > int.MaxValue)
      throw new ArgumentException($"Invalid duration '{text}'; use seconds, 30s, 5m, 6h, or 14d.");
    return Math.Max(1, (int)(n * scale));
  }

  public const string Help = """
ZoneTree WAL / MVCC soak tool (.NET 10)

  run --root PATH --duration 14d [--resume]
  self-test --root PATH
  verify --case PATH

Core options:
  --workers 8 --readers 2 --accounts 128 --slots 128 --seed 173219
  --epoch 5m --long-every 48 --long-epoch 6h --long-rate 25 --snapshot 15s
  --progress 10s --stall 120s --recovery-timeout 180s
  --max-rss-mb 8192 --max-disk-gb 60 --min-free-gb 15 --retain 3
  --cycles N --max-sequence 1000000 --max-outstanding 4096

Stops on the first failure, preserves its evidence, and exits nonzero.
Process kills test process crashes, not physical power loss. Each case also
checks simulated torn power-loss images and native durability/visibility probes.
Progress: ROOT/logs/progress.jsonl. Failures: ROOT/logs/failures.log and .jsonl.
Ctrl+C/SIGTERM requests a clean child stop; a watchdog kills stuck children.
""";
}

sealed record Profile(WriteAheadLogMode Wal, CompressionMethod Compression, DiskSegmentMode Disk, int BlockSize, long ChunkSize, int MutableSize, string Workload)
{
  public override string ToString() => $"{Wal}/{Compression}/{Disk}/block={BlockSize}/chunk={ChunkSize}/mutable={MutableSize}/{Workload}";

  public static List<Profile> Matrix(int seed)
  {
    var profiles = (from wal in new[] { WriteAheadLogMode.Shared, WriteAheadLogMode.AsyncShared }
                    from codec in new[] { CompressionMethod.None, CompressionMethod.LZ4, CompressionMethod.Zstd }
                    from disk in new[] { DiskSegmentMode.SingleDiskSegment, DiskSegmentMode.MultiPartDiskSegment }
                    from block in new[] { 1024, 4096, 128 * 1024, 256 * 1024 }
                    from chunk in new long[] { 256 * 1024, 4 * 1024 * 1024, 64 * 1024 * 1024 }
                    from mutable in new[] { 1024, 8192 }
                    from workload in new[] { "durable", "mixed", "prefix", "queued" }
                    select new Profile(wal, codec, disk, block, chunk, mutable, workload)).ToList();
    var random = new Random(seed);
    for (var i = profiles.Count - 1; i > 0; --i) { var j = random.Next(i + 1); (profiles[i], profiles[j]) = (profiles[j], profiles[i]); }
    return profiles;
  }
}

sealed record CaseConfig(string RunId, long Index, int Seed, string Directory, Profile Profile, Options Options, int Lanes, int Seconds, string Ending)
{
  public string Database => Path.Combine(Directory, "database");
}
