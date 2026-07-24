using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ProfileStore.Benchmark;

public sealed class BenchmarkRunner(BenchmarkConfig config)
{
  readonly ProfileGenerator Generator = new(config.Seed);
  readonly WorkloadPlan Plan = WorkloadPlanner.Create(config, new ProfileGenerator(config.Seed));

  public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(CancellationToken ct)
  {
    Directory.CreateDirectory(config.OutputDirectory);
    Directory.CreateDirectory(config.DataDirectory);
    var results = new List<BenchmarkResult>();
    foreach (var engine in ExpandEngines(config.Engine))
      results.Add(await RunEngineAsync(CreateEngine(engine), ct));
    return results;
  }

  public async Task<BenchmarkResult> RunSingleAsync(CancellationToken ct)
  {
    var engines = ExpandEngines(config.Engine);
    if (engines.Count != 1)
      throw new ArgumentException("Child benchmark runs must select exactly one engine.");
    Directory.CreateDirectory(config.OutputDirectory);
    Directory.CreateDirectory(config.DataDirectory);
    return await RunEngineAsync(CreateEngine(engines[0]), ct);
  }

  async Task<BenchmarkResult> RunEngineAsync(Func<IProfileStoreEngine> engineFactory, CancellationToken ct)
  {
    var runStopwatch = Stopwatch.StartNew();
    await using var memorySampler = ProcessMemorySampler.Start();
    using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    if (config.TimeoutSeconds > 0)
      runCts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
    var runCt = runCts.Token;

    var phases = new List<PhaseResult>();
    var startedAt = DateTime.UtcNow.ToString("O");
    var engineName = "Unknown";
    var durability = "Not available.";
    var completed = false;
    string? status = null;
    double? preReadStabilizeMs = null;
    double? postUpdateStabilizeMs = null;
    double? settleMs = null;
    long? storageSize = null;
    double? reopenMs = null;
    double? verifyMs = null;
    string? verifyChecksum = null;
    string? activeOperation = null;
    string? interruptedPhase = null;

    try
    {
      {
        await using var engine = engineFactory();
        engineName = engine.Name;
        Console.WriteLine();
        Console.WriteLine($"== {engine.Name} ==");
        activeOperation = "initialize";
        await engine.InitializeAsync(config, reset: true, runCt);
        durability = engine.DurabilityDescription;
        var workers = await CreateWorkersAsync(engine, runCt);

        try
        {
          activeOperation = "insert profiles";
          phases.Add(await MeasureInsertPhaseAsync(workers, runCt));

          activeOperation = "pre-read stabilization";
          preReadStabilizeMs = await StabilizeForReadMeasurementsAsync(engine, "pre-read stabilization", runCt);

          activeOperation = "read by user id";
          phases.Add(await MeasureProfileReadPhaseAsync(
              engine,
              workers,
              "read by user id",
              Plan.PointReadIds,
              (worker, id, token) => worker.GetByUserIdAsync(id, token),
              runCt));

          activeOperation = "lookup by email";
          phases.Add(await MeasureProfileReadPhaseAsync(
              engine,
              workers,
              "lookup by email",
              Plan.EmailReadIds,
              (worker, id, token) => worker.GetByEmailAsync(Generator.Create(id).Email, token),
              runCt));

          activeOperation = "scan country/status index";
          phases.Add(await MeasureIndexQueryPhaseAsync(
              workers,
              "scan country/status index",
              Plan.CountryStatusQueries,
              (worker, query) => worker.ScanCountryStatusIndexAsync(query.Country, query.Status, config.QueryLimit, runCt),
              runCt));

          activeOperation = "query country/status";
          phases.Add(await MeasureQueryPhaseAsync(
              workers,
              "query country/status",
              Plan.CountryStatusQueries,
              (worker, query) => worker.QueryCountryStatusAsync(query.Country, query.Status, config.QueryLimit, runCt),
              runCt));

          activeOperation = "scan created-at index";
          phases.Add(await MeasureIndexQueryPhaseAsync(
              workers,
              "scan created-at index",
              Plan.CreatedAtRangeQueries,
              (worker, query) => worker.ScanCreatedAtRangeIndexAsync(query.FromUnixMs, query.ToUnixMs, config.QueryLimit, runCt),
              runCt));

          activeOperation = "query created-at range";
          phases.Add(await MeasureQueryPhaseAsync(
              workers,
              "query created-at range",
              Plan.CreatedAtRangeQueries,
              (worker, query) => worker.QueryCreatedAtRangeAsync(query.FromUnixMs, query.ToUnixMs, config.QueryLimit, runCt),
              runCt));

          activeOperation = "scan top reputation index";
          phases.Add(await MeasureRepeatedIndexQueryPhaseAsync(
              workers,
              "scan top reputation index",
              config.QueryCount,
              worker => worker.ScanTopReputationIndexAsync(config.QueryLimit, runCt),
              runCt));

          activeOperation = "query top reputation";
          phases.Add(await MeasureRepeatedQueryPhaseAsync(
              workers,
              "query top reputation",
              config.QueryCount,
              worker => worker.QueryTopReputationAsync(config.QueryLimit, runCt),
              runCt));

          activeOperation = "update profiles";
          phases.Add(await MeasureUpdatePhaseAsync(engine, workers, runCt));

          activeOperation = "post-update stabilization";
          postUpdateStabilizeMs = await StabilizeForReadMeasurementsAsync(engine, "post-update stabilization", runCt);

          activeOperation = "post-update read by user id";
          phases.Add(await MeasureProfileReadPhaseAsync(
              engine,
              workers,
              "post-update read by user id",
              Plan.PostPointReadIds,
              (worker, id, token) => worker.GetByUserIdAsync(id, token),
              runCt));

          activeOperation = "post-update lookup by email";
          phases.Add(await MeasureProfileReadPhaseAsync(
              engine,
              workers,
              "post-update lookup by email",
              Plan.PostEmailReadIds,
              (worker, id, token) => worker.GetByEmailAsync(Generator.Create(id).Email, token),
              runCt));

          activeOperation = "post-update scan country/status index";
          phases.Add(await MeasureIndexQueryPhaseAsync(
              workers,
              "post-update scan country/status index",
              Plan.PostCountryStatusQueries,
              (worker, query) => worker.ScanCountryStatusIndexAsync(query.Country, query.Status, config.QueryLimit, runCt),
              runCt));

          activeOperation = "post-update query country/status";
          phases.Add(await MeasureQueryPhaseAsync(
              workers,
              "post-update query country/status",
              Plan.PostCountryStatusQueries,
              (worker, query) => worker.QueryCountryStatusAsync(query.Country, query.Status, config.QueryLimit, runCt),
              runCt));

          activeOperation = "post-update scan top reputation index";
          phases.Add(await MeasureRepeatedIndexQueryPhaseAsync(
              workers,
              "post-update scan top reputation index",
              config.PostQueryCount,
              worker => worker.ScanTopReputationIndexAsync(config.QueryLimit, runCt),
              runCt));

          activeOperation = "post-update query top reputation";
          phases.Add(await MeasureRepeatedQueryPhaseAsync(
              workers,
              "post-update query top reputation",
              config.PostQueryCount,
              worker => worker.QueryTopReputationAsync(config.QueryLimit, runCt),
              runCt));
        }
        finally
        {
          await DisposeWorkersAsync(workers);
        }

        activeOperation = "settle";
        (settleMs, _) = await Timing.MeasureAsync(async () =>
        {
          await engine.SettleAsync(runCt);
          return true;
        });
        storageSize = await engine.GetStorageSizeBytesAsync(runCt);
      }

      await using var reopened = engineFactory();
      activeOperation = "reopen";
      (reopenMs, _) = await Timing.MeasureAsync(async () =>
      {
        await reopened.InitializeAsync(config, reset: false, runCt);
        return true;
      });

      activeOperation = "verify";
      (verifyMs, verifyChecksum) = await Timing.MeasureAsync(async () =>
      {
        var checksum = new Checksum();
        checksum.Add(await reopened.CountProfilesAsync(runCt));
        await using var worker = await reopened.CreateWorkerAsync(runCt);
        foreach (var id in new[] { 1L, Math.Max(1, config.Profiles / 2), (long)config.Profiles })
        {
          runCt.ThrowIfCancellationRequested();
          var profile = await worker.GetByUserIdAsync(id, runCt)
              ?? throw new InvalidOperationException($"{reopened.Name} missing profile {id} after reopen");
          checksum.Add(profile);
        }
        return checksum.Hex;
      });
      completed = true;
      status = "Completed";
      activeOperation = null;
    }
    catch (OperationCanceledException) when (runCts.IsCancellationRequested && !ct.IsCancellationRequested)
    {
      status = $"Timed out after {config.TimeoutSeconds:N0} seconds";
      interruptedPhase = activeOperation;
      Console.WriteLine(status);
    }

    runStopwatch.Stop();
    await memorySampler.StopAsync();

    var result = new BenchmarkResult(
        engineName,
        durability,
        startedAt,
        CreateWorkloadConfig(),
        CreateEngineSettings(engineName, config),
        completed,
        status,
        phases,
        runStopwatch.Elapsed.TotalMilliseconds,
        preReadStabilizeMs,
        postUpdateStabilizeMs,
        settleMs,
        storageSize,
        reopenMs,
        verifyMs,
        verifyChecksum,
        memorySampler.PeakWorkingSetBytes,
        GetEnvironment(memorySampler.InitialWorkingSetBytes))
    {
      InterruptedPhase = interruptedPhase
    };

    PrintRunSummary(result);
    return result;
  }

  static void PrintRunSummary(BenchmarkResult result)
  {
    var completedPhaseMs = result.Phases.Sum(x => x.ElapsedMs);
    Console.WriteLine();
    Console.WriteLine($"  run time: {result.RunElapsedMs / 1000:N2}s");
    Console.WriteLine($"  completed phase time: {completedPhaseMs / 1000:N2}s");
    Console.WriteLine(
        $"  process peak memory: {ResultWriter.FormatBytes(result.PeakProcessWorkingSetBytes)}");
    Console.WriteLine();
  }

  BenchmarkWorkloadConfig CreateWorkloadConfig()
  {
    return new BenchmarkWorkloadConfig(
        config.Profiles,
        config.Parallelism,
        config.ReadCount,
        config.EmailReadCount,
        config.QueryCount,
        config.UpdateCount,
        config.PostReadCount,
        config.PostEmailReadCount,
        config.PostQueryCount,
        config.QueryLimit,
        config.Seed,
        config.TimeoutSeconds);
  }

  public static IReadOnlyDictionary<string, string> CreateEngineSettings(string engineName, BenchmarkConfig config)
  {
    return engineName switch
    {
      "ZoneTree" => new Dictionary<string, string>
      {
        ["MutableSegmentMaxItemCount"] = config.ZoneTreeMutableSegmentMaxItemCount.ToString(),
        ["SparseArrayStepSize"] = config.ZoneTreeSparseArrayStepSize.ToString(),
        ["KeyCacheSize"] = config.ZoneTreeKeyCacheSize.ToString(),
        ["ValueCacheSize"] = config.ZoneTreeValueCacheSize.ToString(),
        ["IteratorPrefetchSize"] = config.ZoneTreeIteratorPrefetchSize.ToString(),
        ["BlockCacheLifeTime"] = $"{config.ZoneTreeBlockCacheLifeTimeMinutes} minutes",
        ["BottomMergePolicy"] = "Full bottom merge when bottom segment count exceeds 1",
        ["ReadStabilization"] = "Settle before read/query phases"
      },
      "SQLite" => new Dictionary<string, string>
      {
        ["JournalMode"] = "WAL",
        ["Synchronous"] = "NORMAL",
        ["CacheMb"] = config.SqliteCacheMb.ToString(),
        ["MmapMb"] = config.SqliteMmapMb.ToString(),
        ["TempStore"] = "MEMORY"
      },
      "RocksDB" => new Dictionary<string, string>
      {
        ["Databases"] = "profiles,email-index,country-status-index,created-at-index,reputation-index",
        ["Compression"] = "Zstd",
        ["WriteBufferMb"] = config.RocksDbWriteBufferMb.ToString(),
        ["MaxWriteBufferNumber"] = config.RocksDbMaxWriteBufferNumber.ToString(),
        ["WriteSync"] = "false",
        ["ReadStabilization"] = "Compact before read/query phases"
      },
      "MySQL" => new Dictionary<string, string>
      {
        ["Host"] = config.MySqlHost,
        ["Port"] = config.MySqlPort.ToString(),
        ["Database"] = config.MySqlDatabase,
        ["User"] = config.MySqlUser
      },
      _ => new Dictionary<string, string>()
    };
  }

  static async Task<double?> StabilizeForReadMeasurementsAsync(
      IProfileStoreEngine engine,
      string name,
      CancellationToken ct)
  {
    if (!engine.RequiresReadStabilization)
      return null;

    Console.Write($"  {name}... ");
    var sw = Stopwatch.StartNew();
    await engine.StabilizeForReadMeasurementsAsync(ct);
    sw.Stop();
    Console.WriteLine($"{sw.Elapsed.TotalSeconds:N2}s");
    return sw.Elapsed.TotalMilliseconds;
  }

  async Task<IReadOnlyList<IProfileStoreEngineWorker>> CreateWorkersAsync(
      IProfileStoreEngine engine,
      CancellationToken ct)
  {
    var workers = new List<IProfileStoreEngineWorker>(config.Parallelism);
    try
    {
      for (var i = 0; i < config.Parallelism; i++)
        workers.Add(await engine.CreateWorkerAsync(ct));
      return workers;
    }
    catch
    {
      await DisposeWorkersAsync(workers);
      throw;
    }
  }

  static async Task DisposeWorkersAsync(IEnumerable<IProfileStoreEngineWorker> workers)
  {
    foreach (var worker in workers)
      await worker.DisposeAsync();
  }

  Task<PhaseResult> MeasureInsertPhaseAsync(
      IReadOnlyList<IProfileStoreEngineWorker> workers,
      CancellationToken ct)
  {
    return MeasureParallelPhaseAsync(
        "insert profiles",
        config.Profiles,
        config.Profiles,
        workers,
        async (worker, start, count, checksum) =>
        {
          for (var offset = 0; offset < count; offset++)
          {
            ct.ThrowIfCancellationRequested();
            var userId = start + offset + 1L;
            var profile = Generator.Create(userId);
            await worker.InsertBatchAsync([profile], ct);
            checksum.Add(profile.UserId);
          }
        },
        ct);
  }

  Task<PhaseResult> MeasureUpdatePhaseAsync(
      IProfileStoreEngine engine,
      IReadOnlyList<IProfileStoreEngineWorker> workers,
      CancellationToken ct)
  {
    var buckets = new List<long>[workers.Count];
    for (var i = 0; i < buckets.Length; i++)
      buckets[i] = [];
    foreach (var id in Plan.UpdateIds)
      buckets[(int)(id % workers.Count)].Add(id);

    return MeasureParallelBucketsPhaseAsync(
        "update profiles",
        Plan.UpdateIds.Length,
        workers,
        buckets,
        async (worker, id, checksum) =>
        {
          ct.ThrowIfCancellationRequested();
          var current = await worker.GetByUserIdAsync(id, ct)
              ?? throw new InvalidOperationException($"{engine.Name} missing profile {id}");
          var updated = Generator.Update(current);
          await worker.UpdateBatchAsync([updated], ct);
          checksum.Add(updated);
        },
        ct);
  }

  async Task<PhaseResult> MeasureProfileReadPhaseAsync(
      IProfileStoreEngine engine,
      IReadOnlyList<IProfileStoreEngineWorker> workers,
      string name,
      IReadOnlyList<long> ids,
      Func<IProfileStoreEngineWorker, long, CancellationToken, Task<UserProfile?>> read,
      CancellationToken ct)
  {
    return await MeasureParallelPhaseAsync(
        name,
        ids.Count,
        ids.Count,
        workers,
        async (worker, start, count, checksum) =>
        {
          for (var i = start; i < start + count; i++)
          {
            ct.ThrowIfCancellationRequested();
            var id = ids[i];
            var profile = await read(worker, id, ct)
                ?? throw new InvalidOperationException($"{engine.Name} missing profile {id}");
            checksum.Add(profile);
          }
        },
        ct);
  }

  async Task<PhaseResult> MeasureQueryPhaseAsync<TQuery>(
      IReadOnlyList<IProfileStoreEngineWorker> workers,
      string name,
      IReadOnlyList<TQuery> queries,
      Func<IProfileStoreEngineWorker, TQuery, Task<IReadOnlyList<UserProfile>>> query,
      CancellationToken ct)
  {
    return await MeasureParallelPhaseAsync(
        name,
        queries.Count,
        queries.Count,
        workers,
        async (worker, start, count, checksum) =>
        {
          for (var i = start; i < start + count; i++)
          {
            ct.ThrowIfCancellationRequested();
            var profiles = await query(worker, queries[i]);
            foreach (var profile in profiles)
              checksum.Add(profile);
          }
        },
        ct);
  }

  async Task<PhaseResult> MeasureIndexQueryPhaseAsync<TQuery>(
      IReadOnlyList<IProfileStoreEngineWorker> workers,
      string name,
      IReadOnlyList<TQuery> queries,
      Func<IProfileStoreEngineWorker, TQuery, Task<IReadOnlyList<long>>> query,
      CancellationToken ct)
  {
    return await MeasureParallelPhaseAsync(
        name,
        queries.Count,
        queries.Count,
        workers,
        async (worker, start, count, checksum) =>
        {
          for (var i = start; i < start + count; i++)
          {
            ct.ThrowIfCancellationRequested();
            var userIds = await query(worker, queries[i]);
            foreach (var userId in userIds)
              checksum.Add(userId);
          }
        },
        ct);
  }

  async Task<PhaseResult> MeasureRepeatedQueryPhaseAsync(
      IReadOnlyList<IProfileStoreEngineWorker> workers,
      string name,
      int count,
      Func<IProfileStoreEngineWorker, Task<IReadOnlyList<UserProfile>>> query,
      CancellationToken ct)
  {
    return await MeasureParallelPhaseAsync(
        name,
        count,
        count,
        workers,
        async (worker, _, localCount, checksum) =>
        {
          for (var i = 0; i < localCount; i++)
          {
            ct.ThrowIfCancellationRequested();
            var profiles = await query(worker);
            foreach (var profile in profiles)
              checksum.Add(profile);
          }
        },
        ct);
  }

  async Task<PhaseResult> MeasureRepeatedIndexQueryPhaseAsync(
      IReadOnlyList<IProfileStoreEngineWorker> workers,
      string name,
      int count,
      Func<IProfileStoreEngineWorker, Task<IReadOnlyList<long>>> query,
      CancellationToken ct)
  {
    return await MeasureParallelPhaseAsync(
        name,
        count,
        count,
        workers,
        async (worker, _, localCount, checksum) =>
        {
          for (var i = 0; i < localCount; i++)
          {
            ct.ThrowIfCancellationRequested();
            var userIds = await query(worker);
            foreach (var userId in userIds)
              checksum.Add(userId);
          }
        },
        ct);
  }

  async Task<PhaseResult> MeasureParallelPhaseAsync(
      string name,
      long operations,
      int partitionedOperations,
      IReadOnlyList<IProfileStoreEngineWorker> workers,
      Func<IProfileStoreEngineWorker, int, int, Checksum, Task> action,
      CancellationToken ct)
  {
    Console.Write($"  {name}... ");
    var startGate = new ParallelPhaseStartGate(workers.Count);
    var tasks = new Task<string?>[workers.Count];
    for (var workerIndex = 0; workerIndex < workers.Count; workerIndex++)
    {
      var index = workerIndex;
      var (start, count) = GetPartition(partitionedOperations, index, workers.Count);
      tasks[index] = Task.Run(async () =>
      {
        await startGate.WaitAsync(ct);
        if (count == 0)
          return null;
        var checksum = new Checksum();
        await action(workers[index], start, count, checksum);
        return checksum.Hex;
      });
    }
    await startGate.WaitUntilReadyAsync();
    var sw = Stopwatch.StartNew();
    startGate.Release();
    var checksums = await Task.WhenAll(tasks);
    sw.Stop();
    var throughput = operations / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
    Console.WriteLine($"{sw.Elapsed.TotalSeconds:N2}s ({throughput:N0}/s)");
    return new PhaseResult(name, operations, sw.Elapsed.TotalMilliseconds, throughput, CombineChecksums(checksums));
  }

  async Task<PhaseResult> MeasureParallelBucketsPhaseAsync<T>(
      string name,
      long operations,
      IReadOnlyList<IProfileStoreEngineWorker> workers,
      IReadOnlyList<IReadOnlyList<T>> buckets,
      Func<IProfileStoreEngineWorker, T, Checksum, Task> action,
      CancellationToken ct)
  {
    Console.Write($"  {name}... ");
    var startGate = new ParallelPhaseStartGate(workers.Count);
    var tasks = new Task<string?>[workers.Count];
    for (var workerIndex = 0; workerIndex < workers.Count; workerIndex++)
    {
      var index = workerIndex;
      tasks[index] = Task.Run(async () =>
      {
        await startGate.WaitAsync(ct);
        if (buckets[index].Count == 0)
          return null;
        var checksum = new Checksum();
        foreach (var item in buckets[index])
          await action(workers[index], item, checksum);
        return checksum.Hex;
      });
    }
    await startGate.WaitUntilReadyAsync();
    var sw = Stopwatch.StartNew();
    startGate.Release();
    var checksums = await Task.WhenAll(tasks);
    sw.Stop();
    var throughput = operations / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
    Console.WriteLine($"{sw.Elapsed.TotalSeconds:N2}s ({throughput:N0}/s)");
    return new PhaseResult(name, operations, sw.Elapsed.TotalMilliseconds, throughput, CombineChecksums(checksums));
  }

  static (int Start, int Count) GetPartition(int total, int index, int partitions)
  {
    var baseCount = total / partitions;
    var remainder = total % partitions;
    var count = baseCount + (index < remainder ? 1 : 0);
    var start = index * baseCount + Math.Min(index, remainder);
    return (start, count);
  }

  static string CombineChecksums(IReadOnlyList<string?> checksums)
  {
    var active = checksums.Where(x => x != null).ToArray();
    if (active.Length == 1)
      return active[0]!;
    var checksum = new Checksum();
    foreach (var value in active)
      checksum.Add(value!);
    return checksum.Hex;
  }

  public static IReadOnlyList<string> ExpandEngines(string engine)
  {
    var requested = engine
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.ToLowerInvariant())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (requested.Contains("all"))
      requested = ["zonetree", "rocksdb", "sqlite", "mysql"];

    var engines = new List<string>();
    if (requested.Contains("zonetree"))
      engines.Add("zonetree");
    if (requested.Contains("rocksdb"))
      engines.Add("rocksdb");
    if (requested.Contains("sqlite"))
      engines.Add("sqlite");
    if (requested.Contains("mysql"))
      engines.Add("mysql");
    if (engines.Count == 0)
      throw new ArgumentException($"No supported engine selected: {engine}");
    return engines;
  }

  static Func<IProfileStoreEngine> CreateEngine(string engine)
  {
    return engine switch
    {
      "zonetree" => () => new ZoneTreeProfileStore(),
      "rocksdb" => () => new RocksDbProfileStore(),
      "sqlite" => () => new SqliteProfileStore(),
      "mysql" => () => new MySqlProfileStore(),
      _ => throw new ArgumentException($"Unsupported engine: {engine}")
    };
  }

  public static string GetEnvironment(long? initialProcessWorkingSetBytes)
  {
    return string.Join(Environment.NewLine, CreateEnvironmentLines(initialProcessWorkingSetBytes));
  }

  static IReadOnlyList<string> CreateEnvironmentLines(long? initialProcessWorkingSetBytes)
  {
    var packageVersions = GetResolvedPackageVersions();
    return
    [
        $"* OS: {RuntimeInformation.OSDescription}",
        $"* Architecture: {RuntimeInformation.ProcessArchitecture}",
        $"* .NET: {Environment.Version}",
        $"* CPU: {GetCpuDescription()}",
        $"* Logical processors: {Environment.ProcessorCount}",
        $"* Total available memory: {FormatOptionalBytes(GetTotalAvailableMemoryBytes())}",
        $"* Initial process working set: {FormatOptionalBytes(initialProcessWorkingSetBytes)}",
        $"* Benchmark version: {GetAssemblyVersion(Assembly.GetEntryAssembly())}",
        $"* ZoneTree version: {ZoneTree.Core.ZoneTreeInfo.ProductVersion}",
        $"* Microsoft.Data.Sqlite version: {GetPackageVersion(packageVersions, "Microsoft.Data.Sqlite", typeof(SqliteConnection).Assembly)}",
        $"* SQLite runtime version: {GetSqliteRuntimeVersion()}",
        $"* SQLitePCLRaw.core version: {GetPackageVersion(packageVersions, "SQLitePCLRaw.core")}",
        $"* SQLitePCLRaw.lib.e_sqlite3 version: {GetPackageVersion(packageVersions, "SQLitePCLRaw.lib.e_sqlite3")}",
        $"* RocksDbSharp version: {GetPackageVersion(packageVersions, "RocksDbSharp")}",
        $"* RocksDbNative version: {GetPackageVersion(packageVersions, "RocksDbNative")}",
        $"* MySqlConnector version: {GetPackageVersion(packageVersions, "MySqlConnector")}"
    ];
  }

  static string GetAssemblyVersion(Assembly? assembly)
  {
    return assembly?.GetName().Version?.ToString() ?? "n/a";
  }

  static string GetPackageVersion(
      IReadOnlyDictionary<string, string> packageVersions,
      string packageName,
      Assembly? fallbackAssembly = null)
  {
    return packageVersions.TryGetValue(packageName, out var version)
        ? version
        : GetAssemblyVersion(fallbackAssembly);
  }

  static IReadOnlyDictionary<string, string> GetResolvedPackageVersions()
  {
    try
    {
      var assemblyPath = Assembly.GetEntryAssembly()?.Location;
      if (string.IsNullOrWhiteSpace(assemblyPath))
        return new Dictionary<string, string>();

      var depsPath = Path.ChangeExtension(assemblyPath, ".deps.json");
      if (!File.Exists(depsPath))
        return new Dictionary<string, string>();

      using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
      if (!document.RootElement.TryGetProperty("libraries", out var libraries))
        return new Dictionary<string, string>();

      var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var library in libraries.EnumerateObject())
      {
        if (!library.Value.TryGetProperty("type", out var type) ||
            !string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase))
          continue;

        var separator = library.Name.LastIndexOf('/');
        if (separator <= 0 || separator == library.Name.Length - 1)
          continue;

        versions[library.Name[..separator]] = library.Name[(separator + 1)..];
      }
      return versions;
    }
    catch
    {
      return new Dictionary<string, string>();
    }
  }

  static string GetSqliteRuntimeVersion()
  {
    try
    {
      using var connection = new SqliteConnection("Data Source=:memory:");
      connection.Open();
      using var command = connection.CreateCommand();
      command.CommandText = "SELECT sqlite_version();";
      return command.ExecuteScalar()?.ToString() ?? "n/a";
    }
    catch
    {
      return "n/a";
    }
  }

  static string GetCpuDescription()
  {
    try
    {
      if (OperatingSystem.IsWindows())
      {
        var value = Microsoft.Win32.Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "ProcessorNameString",
            null);
        return value?.ToString()?.Trim() ?? "n/a";
      }

      if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
      {
        foreach (var line in File.ReadLines("/proc/cpuinfo"))
        {
          if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
          {
            var separator = line.IndexOf(':');
            if (separator >= 0)
              return line[(separator + 1)..].Trim();
          }
        }
      }

      if (OperatingSystem.IsMacOS())
        return RunProcessForSingleLine("sysctl", "-n machdep.cpu.brand_string") ?? "n/a";
    }
    catch
    {
    }
    return "n/a";
  }

  static long? GetTotalAvailableMemoryBytes()
  {
    var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    return memory > 0 ? memory : null;
  }

  static string FormatOptionalBytes(long? bytes) =>
      bytes.HasValue ? ResultWriter.FormatBytes(bytes.Value) : "n/a";

  static string? RunProcessForSingleLine(string fileName, string arguments)
  {
    try
    {
      using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
      {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
      });
      if (process == null)
        return null;
      var line = process.StandardOutput.ReadLine();
      process.WaitForExit(1000);
      return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
    }
    catch
    {
      return null;
    }
  }

  sealed class ParallelPhaseStartGate
  {
    readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource Start = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int Remaining;

    public ParallelPhaseStartGate(int participantCount)
    {
      Remaining = participantCount;
    }

    public async Task WaitAsync(CancellationToken ct)
    {
      if (Interlocked.Decrement(ref Remaining) == 0)
        Ready.TrySetResult();
      await Start.Task.WaitAsync(ct);
    }

    public Task WaitUntilReadyAsync() => Ready.Task;

    public void Release() => Start.TrySetResult();
  }
}
