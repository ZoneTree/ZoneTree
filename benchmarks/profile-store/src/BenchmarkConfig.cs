using System.Globalization;
using System.Text.Json.Serialization;

namespace ProfileStore.Benchmark;

public sealed record BenchmarkConfig
{
  public const int MinimumZoneTreeMutableSegmentMaxItemCount = 1_000;

  public string Engine { get; init; } = "all";

  public int Profiles { get; init; } = 100_000;

  [JsonIgnore]
  public IReadOnlyList<int> ProfileCounts { get; init; } = [100_000];

  public int Parallelism { get; init; } = 1;

  [JsonIgnore]
  public IReadOnlyList<int> ParallelismLevels { get; init; } = [1];

  public int ReadCount { get; init; } = -1;

  public int EmailReadCount { get; init; } = -1;

  public int QueryCount { get; init; } = -1;

  public int UpdateCount { get; init; } = -1;

  public int PostReadCount { get; init; } = -1;

  public int PostEmailReadCount { get; init; } = -1;

  public int PostQueryCount { get; init; } = -1;

  public int QueryLimit { get; init; } = 50;

  public int Seed { get; init; } = 570123434;

  public int TimeoutSeconds { get; init; }

  public string OutputDirectory { get; init; } = "results";

  public string DataDirectory { get; init; } = "data";

  public int ZoneTreeMutableSegmentMaxItemCount { get; init; } = 250_000;

  public int ZoneTreeBlockCacheLifetimeMinutes { get; init; } = 1;

  public int ZoneTreeSparseArrayStepSize { get; init; } = 16;

  public int ZoneTreeKeyCacheSize { get; init; } = 1_024;

  public int ZoneTreeValueCacheSize { get; init; } = 8_192;

  public int ZoneTreeIteratorPrefetchSize { get; init; } = 16;

  public int SqliteCacheMb { get; init; } = 1_024;

  public int SqliteMmapMb { get; init; } = 1_024;

  public int RocksDbWriteBufferMb { get; init; } = 1024;

  public int RocksDbMaxWriteBufferNumber { get; init; } = 4;

  [JsonIgnore]
  public bool CleanData { get; init; }

  [JsonIgnore]
  public bool CleanResults { get; init; }

  [JsonIgnore]
  public bool UpdateLatest { get; init; }

  [JsonIgnore]
  public bool ChildRun { get; init; }

  [JsonIgnore]
  public string? ResultFile { get; init; }

  public string MySqlHost { get; init; } =
      Environment.GetEnvironmentVariable("MYSQL_HOST") ?? "localhost";

  public int MySqlPort { get; init; } =
      int.TryParse(Environment.GetEnvironmentVariable("MYSQL_PORT"), out var port) ? port : 3306;

  public string MySqlDatabase { get; init; } =
      Environment.GetEnvironmentVariable("MYSQL_DATABASE") ?? "profilebench";

  public string MySqlUser { get; init; } =
      Environment.GetEnvironmentVariable("MYSQL_USER") ?? "root";

  public string MySqlPassword { get; init; } =
      Environment.GetEnvironmentVariable("MYSQL_PASSWORD") ?? "root";

  public static BenchmarkConfig Parse(string[] args)
  {
    var config = new BenchmarkConfig();
    for (var i = 0; i < args.Length; i++)
    {
      string next()
      {
        if (i + 1 >= args.Length)
          throw new ArgumentException($"Missing value for {args[i]}");
        return args[++i];
      }

      config = args[i] switch
      {
        "--engine" => config with { Engine = next() },
        "--profiles" => config.WithProfileCounts(next()),
        "--parallelism" => config.WithParallelismLevels(next()),
        "--read-count" => config with { ReadCount = int.Parse(next()) },
        "--email-read-count" => config with { EmailReadCount = int.Parse(next()) },
        "--query-count" => config with { QueryCount = int.Parse(next()) },
        "--update-count" => config with { UpdateCount = int.Parse(next()) },
        "--post-read-count" => config with { PostReadCount = int.Parse(next()) },
        "--post-email-read-count" => config with { PostEmailReadCount = int.Parse(next()) },
        "--post-query-count" => config with { PostQueryCount = int.Parse(next()) },
        "--query-limit" => config with { QueryLimit = int.Parse(next()) },
        "--seed" => config with { Seed = int.Parse(next()) },
        "--timeout-seconds" => config with { TimeoutSeconds = int.Parse(next()) },
        "--output" => config with { OutputDirectory = next() },
        "--data" => config with { DataDirectory = next() },
        "--zonetree-mutable-segment-max-item-count" => config with { ZoneTreeMutableSegmentMaxItemCount = int.Parse(next()) },
        "--zonetree-block-cache-lifetime-minutes" => config with { ZoneTreeBlockCacheLifetimeMinutes = int.Parse(next()) },
        "--zonetree-sparse-array-step-size" => config with { ZoneTreeSparseArrayStepSize = int.Parse(next()) },
        "--zonetree-key-cache-size" => config with { ZoneTreeKeyCacheSize = int.Parse(next()) },
        "--zonetree-value-cache-size" => config with { ZoneTreeValueCacheSize = int.Parse(next()) },
        "--zonetree-iterator-prefetch-size" => config with { ZoneTreeIteratorPrefetchSize = int.Parse(next()) },
        "--sqlite-cache-mb" => config with { SqliteCacheMb = int.Parse(next()) },
        "--sqlite-mmap-mb" => config with { SqliteMmapMb = int.Parse(next()) },
        "--rocksdb-write-buffer-mb" => config with { RocksDbWriteBufferMb = int.Parse(next()) },
        "--rocksdb-max-write-buffer-number" => config with { RocksDbMaxWriteBufferNumber = int.Parse(next()) },
        "--clean" => config with { CleanData = true, CleanResults = true },
        "--clean-data" => config with { CleanData = true },
        "--clean-results" => config with { CleanResults = true },
        "--update-latest" => config with { UpdateLatest = true },
        "--child-run" => config with { ChildRun = true },
        "--result-file" => config with { ResultFile = next() },
        "--mysql-host" => config with { MySqlHost = next() },
        "--mysql-port" => config with { MySqlPort = int.Parse(next()) },
        "--mysql-database" => config with { MySqlDatabase = next() },
        "--mysql-user" => config with { MySqlUser = next() },
        "--mysql-password" => config with { MySqlPassword = next() },
        "--help" or "-h" => throw new HelpRequestedException(),
        _ => throw new ArgumentException($"Unknown argument: {args[i]}")
      };
    }

    foreach (var profiles in config.ProfileCounts)
    {
      if (profiles <= 0)
        throw new ArgumentOutOfRangeException(nameof(config.Profiles));
    }
    foreach (var parallelism in config.ParallelismLevels)
    {
      if (parallelism <= 0)
        throw new ArgumentOutOfRangeException(nameof(config.Parallelism));
    }
    if (config.TimeoutSeconds < 0)
      throw new ArgumentOutOfRangeException(nameof(config.TimeoutSeconds));
    ValidateOptionalCount(config.ReadCount, nameof(ReadCount));
    ValidateOptionalCount(config.EmailReadCount, nameof(EmailReadCount));
    ValidateOptionalCount(config.QueryCount, nameof(QueryCount));
    ValidateOptionalCount(config.UpdateCount, nameof(UpdateCount));
    ValidateOptionalCount(config.PostReadCount, nameof(PostReadCount));
    ValidateOptionalCount(config.PostEmailReadCount, nameof(PostEmailReadCount));
    ValidateOptionalCount(config.PostQueryCount, nameof(PostQueryCount));
    if (config.QueryLimit <= 0)
      throw new ArgumentOutOfRangeException(nameof(config.QueryLimit));
    if (config.ZoneTreeMutableSegmentMaxItemCount < MinimumZoneTreeMutableSegmentMaxItemCount)
      throw new ArgumentOutOfRangeException(
          nameof(config.ZoneTreeMutableSegmentMaxItemCount),
          $"Value must be greater than or equal to {MinimumZoneTreeMutableSegmentMaxItemCount}.");
    if (config.ZoneTreeBlockCacheLifetimeMinutes <= 0)
      throw new ArgumentOutOfRangeException(nameof(config.ZoneTreeBlockCacheLifetimeMinutes));
    if (config.ZoneTreeSparseArrayStepSize < 0)
      throw new ArgumentOutOfRangeException(nameof(config.ZoneTreeSparseArrayStepSize));
    if (config.ZoneTreeKeyCacheSize < 0)
      throw new ArgumentOutOfRangeException(nameof(config.ZoneTreeKeyCacheSize));
    if (config.ZoneTreeValueCacheSize < 0)
      throw new ArgumentOutOfRangeException(nameof(config.ZoneTreeValueCacheSize));
    if (config.ZoneTreeIteratorPrefetchSize < 0)
      throw new ArgumentOutOfRangeException(nameof(config.ZoneTreeIteratorPrefetchSize));
    if (config.SqliteCacheMb < 0)
      throw new ArgumentOutOfRangeException(nameof(config.SqliteCacheMb));
    if (config.SqliteMmapMb < 0)
      throw new ArgumentOutOfRangeException(nameof(config.SqliteMmapMb));
    if (config.RocksDbWriteBufferMb <= 0)
      throw new ArgumentOutOfRangeException(nameof(config.RocksDbWriteBufferMb));
    if (config.RocksDbMaxWriteBufferNumber <= 0)
      throw new ArgumentOutOfRangeException(nameof(config.RocksDbMaxWriteBufferNumber));
    return config;
  }

  BenchmarkConfig WithProfileCounts(string value)
  {
    var counts = value
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(ParseProfileCount)
        .ToArray();
    if (counts.Length == 0)
      throw new ArgumentException("At least one profile count is required.", nameof(value));
    return this with { Profiles = counts[0], ProfileCounts = counts };
  }

  BenchmarkConfig WithParallelismLevels(string value)
  {
    var levels = value
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(int.Parse)
        .ToArray();
    if (levels.Length == 0)
      throw new ArgumentException("At least one parallelism level is required.", nameof(value));
    return this with { Parallelism = levels[0], ParallelismLevels = levels };
  }

  public BenchmarkConfig ForProfileCount(int profiles) =>
      (this with { Profiles = profiles, ProfileCounts = [profiles] }).ApplyDefaultWorkloadCounts();

  public BenchmarkConfig ForParallelism(int parallelism) =>
      this with { Parallelism = parallelism, ParallelismLevels = [parallelism] };

  BenchmarkConfig ApplyDefaultWorkloadCounts()
  {
    var defaultQueryCount = Profiles <= 10_000 ? Profiles : Math.Max(1, Profiles / 4);
    return this with
    {
      ReadCount = ReadCount < 0 ? Profiles : ReadCount,
      EmailReadCount = EmailReadCount < 0 ? Profiles : EmailReadCount,
      QueryCount = QueryCount < 0 ? defaultQueryCount : QueryCount,
      UpdateCount = UpdateCount < 0 ? Profiles : UpdateCount,
      PostReadCount = PostReadCount < 0 ? Profiles : PostReadCount,
      PostEmailReadCount = PostEmailReadCount < 0 ? Profiles : PostEmailReadCount,
      PostQueryCount = PostQueryCount < 0 ? defaultQueryCount : PostQueryCount,
    };
  }

  static int ParseProfileCount(string value)
  {
    value = value.Trim().Replace("_", "", StringComparison.Ordinal);
    if (value.Length == 0)
      throw new ArgumentException("Profile count cannot be empty.", nameof(value));

    var multiplier = 1_000_000m;
    var numberText = value;
    var suffix = char.ToUpperInvariant(value[^1]);
    if (suffix == 'K')
    {
      multiplier = 1_000m;
      numberText = value[..^1];
    }
    else if (suffix == 'M')
    {
      multiplier = 1_000_000m;
      numberText = value[..^1];
    }
    else
    {
      multiplier = 1m;
    }

    var count = decimal.Parse(numberText, NumberStyles.Number, CultureInfo.InvariantCulture) * multiplier;
    if (count <= 0 || count > int.MaxValue || count != decimal.Truncate(count))
      throw new ArgumentOutOfRangeException(nameof(value), $"Invalid profile count: {value}");
    return (int)count;
  }

  static void ValidateOptionalCount(int value, string name)
  {
    if (value < -1)
      throw new ArgumentOutOfRangeException(name);
  }

  public IEnumerable<string> ToArguments(string engine, bool childRun, string? resultFile)
  {
    yield return "--engine";
    yield return engine;
    yield return "--profiles";
    yield return Profiles.ToString();
    yield return "--parallelism";
    yield return Parallelism.ToString();
    yield return "--read-count";
    yield return ReadCount.ToString();
    yield return "--email-read-count";
    yield return EmailReadCount.ToString();
    yield return "--query-count";
    yield return QueryCount.ToString();
    yield return "--update-count";
    yield return UpdateCount.ToString();
    yield return "--post-read-count";
    yield return PostReadCount.ToString();
    yield return "--post-email-read-count";
    yield return PostEmailReadCount.ToString();
    yield return "--post-query-count";
    yield return PostQueryCount.ToString();
    yield return "--query-limit";
    yield return QueryLimit.ToString();
    yield return "--seed";
    yield return Seed.ToString();
    yield return "--timeout-seconds";
    yield return TimeoutSeconds.ToString();
    yield return "--output";
    yield return OutputDirectory;
    yield return "--data";
    yield return DataDirectory;
    yield return "--zonetree-mutable-segment-max-item-count";
    yield return ZoneTreeMutableSegmentMaxItemCount.ToString();
    yield return "--zonetree-block-cache-lifetime-minutes";
    yield return ZoneTreeBlockCacheLifetimeMinutes.ToString();
    yield return "--zonetree-sparse-array-step-size";
    yield return ZoneTreeSparseArrayStepSize.ToString();
    yield return "--zonetree-key-cache-size";
    yield return ZoneTreeKeyCacheSize.ToString();
    yield return "--zonetree-value-cache-size";
    yield return ZoneTreeValueCacheSize.ToString();
    yield return "--zonetree-iterator-prefetch-size";
    yield return ZoneTreeIteratorPrefetchSize.ToString();
    yield return "--sqlite-cache-mb";
    yield return SqliteCacheMb.ToString();
    yield return "--sqlite-mmap-mb";
    yield return SqliteMmapMb.ToString();
    yield return "--rocksdb-write-buffer-mb";
    yield return RocksDbWriteBufferMb.ToString();
    yield return "--rocksdb-max-write-buffer-number";
    yield return RocksDbMaxWriteBufferNumber.ToString();
    yield return "--mysql-host";
    yield return MySqlHost;
    yield return "--mysql-port";
    yield return MySqlPort.ToString();
    yield return "--mysql-database";
    yield return MySqlDatabase;
    yield return "--mysql-user";
    yield return MySqlUser;
    yield return "--mysql-password";
    yield return MySqlPassword;

    if (childRun)
      yield return "--child-run";
    if (resultFile != null)
    {
      yield return "--result-file";
      yield return resultFile;
    }
  }
}

public sealed class HelpRequestedException : Exception
{
}
