using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using RocksDbSharp;
using ZoneTree;
using ZoneTree.Collections.BTree.Lock;
using ZoneTree.Comparers;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Serializers;

var config = ScanLabConfig.Parse(args).WithDefaults();
Console.WriteLine("ZoneTree Scan Lab");
Console.WriteLine($"engine={config.Engine}; mode={config.Mode}; scan={config.Scan}; profiles={FormatCount(config.Profiles)}; scan-count={FormatCount(config.ScanCount)}; limit={config.Limit}; parallelism={config.Parallelism}");
Console.WriteLine($"prefetch={config.PrefetchSize}");
Console.WriteLine($"btree-lock={config.BTreeLockMode}");
Console.WriteLine($"created-at={config.CreatedAtMode}");
Console.WriteLine($"read-order={config.ReadOrder}");
Console.WriteLine($"reuse-iterator={config.ReuseIterator}");
Console.WriteLine($"data={Path.GetFullPath(config.EngineDataDirectory)}");

if (config.Reset && Directory.Exists(config.EngineDataDirectory))
  Directory.Delete(config.EngineDataDirectory, recursive: true);
Directory.CreateDirectory(config.EngineDataDirectory);

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    Console.WriteLine($"peak working set={System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024 * 1024):N2} GB");
await using var store = CreateStore(config);
store.Open();

if (config.Mode is RunMode.Insert or RunMode.All)
{
  await store.InsertAsync(config.Profiles);
  await store.SettleAsync();
}

if (config.Mode is RunMode.Alloc)
{
  await store.SettleAsync();
  ((ZoneTreeScanStore)store).ProfileScanAllocations(10_000);
}

if (config.Mode is RunMode.Read or RunMode.TryGet or RunMode.RepeatedTryGet or RunMode.RepeatedDiskTryGet or RunMode.Seek or RunMode.RepeatedSeek or RunMode.PrefixSeek or RunMode.Scan or RunMode.Query or RunMode.All)
{
  if (!store.HasData())
    throw new InvalidOperationException("No persisted data found. Run --mode insert first, or use --mode all.");

  await store.SettleAsync();
  var plan = ScanPlan.Create(config, store.Generator);
  for (var i = 0; i < 5; ++i)
  {
    if (config.Mode is RunMode.Read or RunMode.All)
      await store.RunProfileReadAsync(plan.ProfileReads);

    if ((config.Mode is RunMode.TryGet) &&
        (config.Scan is ScanKind.CountryStatus or ScanKind.All))
      await store.RunCountryStatusTryGetAsync(plan.CountryStatusKeys);

    if ((config.Mode is RunMode.RepeatedTryGet) &&
        (config.Scan is ScanKind.CountryStatus or ScanKind.All))
      await store.RunCountryStatusRepeatedTryGetAsync(plan.RepeatedCountryStatusKeys);

    if ((config.Mode is RunMode.RepeatedDiskTryGet) &&
        (config.Scan is ScanKind.CountryStatus or ScanKind.All))
      await store.RunCountryStatusRepeatedDiskTryGetAsync(plan.RepeatedCountryStatusKeys);

    if ((config.Mode is RunMode.Seek) &&
        (config.Scan is ScanKind.CountryStatus or ScanKind.All))
      await store.RunCountryStatusSeekAsync(plan.CountryStatusKeys);

    if ((config.Mode is RunMode.RepeatedSeek) &&
        (config.Scan is ScanKind.CountryStatus or ScanKind.All))
      await store.RunCountryStatusRepeatedSeekAsync(plan.RepeatedCountryStatusKeys);

    if ((config.Mode is RunMode.PrefixSeek) &&
        (config.Scan is ScanKind.CountryStatus or ScanKind.All))
      await store.RunCountryStatusPrefixSeekAsync(plan.CountryStatusPrefixes);

    if ((config.Mode is RunMode.Scan or RunMode.All) &&
        (config.Scan is ScanKind.CountryStatus or ScanKind.All))
      await store.RunCountryStatusScanAsync(plan.CountryStatusQueries);

    if ((config.Mode is RunMode.Query or RunMode.All) &&
        (config.Scan is ScanKind.CountryStatus or ScanKind.All))
      await store.RunCountryStatusQueryAsync(plan.CountryStatusQueries);

    if ((config.Mode is RunMode.Scan or RunMode.All) &&
        (config.Scan is ScanKind.CreatedAt or ScanKind.All))
      await store.RunCreatedAtScanAsync(plan.CreatedAtQueries);

    if ((config.Mode is RunMode.Query or RunMode.All) &&
        (config.Scan is ScanKind.CreatedAt or ScanKind.All))
      await store.RunCreatedAtQueryAsync(plan.CreatedAtQueries);
  }

  if ((config.Mode is RunMode.Scan or RunMode.All) &&
      (config.Scan is ScanKind.TopReputation or ScanKind.All))
    await store.RunTopReputationScanAsync();

  if ((config.Mode is RunMode.Query or RunMode.All) &&
      (config.Scan is ScanKind.TopReputation or ScanKind.All))
    await store.RunTopReputationQueryAsync();
}

static IScanStore CreateStore(ScanLabConfig config) =>
    config.Engine switch
    {
      EngineKind.ZoneTree => new ZoneTreeScanStore(config),
      EngineKind.RocksDb => new RocksDbScanStore(config),
      _ => throw new ArgumentOutOfRangeException(nameof(config.Engine))
    };

static string FormatCount(int count)
{
  if (count % 1_000_000 == 0)
    return $"{count / 1_000_000}M";
  if (count % 1_000 == 0)
    return $"{count / 1_000}K";
  return count.ToString("N0");
}

interface IScanStore : IAsyncDisposable
{
  ProfileGenerator Generator { get; }

  void Open();

  bool HasData();

  Task InsertAsync(int profiles);

  Task SettleAsync();

  Task RunProfileReadAsync(ProfileReadPlan plan);

  Task RunCountryStatusTryGetAsync(CountryStatusKey[] keys);

  Task RunCountryStatusRepeatedTryGetAsync(CountryStatusKey[] keys);

  Task RunCountryStatusRepeatedDiskTryGetAsync(CountryStatusKey[] keys);

  Task RunCountryStatusSeekAsync(CountryStatusKey[] keys);

  Task RunCountryStatusRepeatedSeekAsync(CountryStatusKey[] keys);

  Task RunCountryStatusPrefixSeekAsync(CountryStatusKey[] prefixes);

  Task RunCountryStatusScanAsync(CountryStatusQuery[] queries);

  Task RunCountryStatusQueryAsync(CountryStatusQuery[] queries);

  Task RunCreatedAtScanAsync(CreatedAtQuery[] queries);

  Task RunCreatedAtQueryAsync(CreatedAtQuery[] queries);

  Task RunTopReputationScanAsync();

  Task RunTopReputationQueryAsync();
}

sealed class ZoneTreeScanStore(ScanLabConfig config) : IScanStore
{
  public ProfileGenerator Generator { get; } = new(config.Seed);

  IZoneTree<long, string>? Profiles;
  IZoneTree<string, long>? CountryStatusIndex;
  IZoneTree<string, long>? CreatedAtIndex;
  IZoneTree<string, long>? ReputationIndex;
  readonly List<IMaintainer> Maintainers = [];
  string ProfilesPath => Path.Combine(config.EngineDataDirectory, "profiles");
  string CountryStatusPath => Path.Combine(config.EngineDataDirectory, "country-status-index");
  string CreatedAtPath => Path.Combine(config.EngineDataDirectory, "created-at-index");
  string ReputationPath => Path.Combine(config.EngineDataDirectory, "reputation-index");

  public void Open()
  {
    Profiles = OpenLongStringTree(ProfilesPath);
    CountryStatusIndex = OpenStringLongTree(CountryStatusPath);
    CreatedAtIndex = OpenStringLongTree(CreatedAtPath);
    ReputationIndex = OpenStringLongTree(ReputationPath);
    AddMaintainer(Profiles);
    AddMaintainer(CountryStatusIndex);
    AddMaintainer(CreatedAtIndex);
    AddMaintainer(ReputationIndex);
  }

  public bool HasData() =>
      HasPersistedFiles(ProfilesPath) ||
      HasPersistedFiles(CountryStatusPath) ||
      HasPersistedFiles(CreatedAtPath) ||
      HasPersistedFiles(ReputationPath);

  public async Task InsertAsync(int profiles)
  {
    Console.Write("insert indexes... ");
    var sw = Stopwatch.StartNew();
    for (var id = 1; id <= profiles; id++)
    {
      var profile = Generator.Create(id);
      Profiles!.Upsert(profile.UserId, ProfileCodec.Serialize(profile));
      CountryStatusIndex!.Upsert(ProfileKeys.CountryStatus(profile), profile.UserId);
      CreatedAtIndex!.Upsert(ProfileKeys.CreatedAt(profile), profile.UserId);
      ReputationIndex!.Upsert(ProfileKeys.Reputation(profile), profile.UserId);
    }
    sw.Stop();
    Console.WriteLine($"{sw.Elapsed.TotalMilliseconds:N0} ms ({profiles / Math.Max(sw.Elapsed.TotalSeconds, 0.001):N0}/s)");
    await Task.CompletedTask;
  }

  public async Task SettleAsync()
  {
    Console.Write("settle... ");
    var sw = Stopwatch.StartNew();
    foreach (var maintainer in Maintainers)
      maintainer.EvictToDisk();
    foreach (var maintainer in Maintainers)
      await maintainer.WaitForBackgroundThreadsAsync();
    SaveMetaData();
    sw.Stop();
    Console.WriteLine($"{sw.Elapsed.TotalMilliseconds:N0} ms");
    PrintLayout("profiles", Profiles!);
    PrintLayout("country/status", CountryStatusIndex!);
    PrintLayout("created-at", CreatedAtIndex!);
    PrintLayout("reputation", ReputationIndex!);
  }

  /// <summary>Shows where a tree's records are, so a run never measures the mutable segment by mistake.</summary>
  static void PrintLayout<TKey, TValue>(string name, IZoneTree<TKey, TValue> tree)
  {
    var maintenance = tree.Maintenance;
    var bottom = maintenance.BottomSegments;
    Console.WriteLine(
        $"  layout {name}: mutable={maintenance.MutableSegment.Length:N0}; " +
        $"read-only={maintenance.ReadOnlySegmentsCount} ({maintenance.ReadOnlySegmentsRecordCount:N0}); " +
        $"disk={maintenance.DiskSegment.Length:N0}; " +
        $"bottom={bottom.Count} ({bottom.Sum(segment => segment.Length):N0})");
  }

  public Task RunProfileReadAsync(ProfileReadPlan plan) =>
      ScanExecution.RunPartitionedAsync(
          $"read profiles ({config.ReadOrder.ToString().ToLowerInvariant()})",
          plan.UserIds.Length,
          config.Parallelism,
          (start, count, checksum) =>
          {
            for (var i = start; i < start + count; i++)
            {
              if (TryGetProfile(plan.UserIds[i], out var profile))
                checksum.Add(profile);
            }
          });

  public Task RunCountryStatusTryGetAsync(CountryStatusKey[] keys) =>
      RunCountryStatusTryGetAsync("try-get country/status", keys);

  public Task RunCountryStatusRepeatedTryGetAsync(CountryStatusKey[] keys) =>
      RunCountryStatusTryGetAsync("repeated-try-get country/status", keys);

  public Task RunCountryStatusRepeatedDiskTryGetAsync(CountryStatusKey[] keys)
  {
    var diskSegment = CountryStatusIndex!.Maintenance.DiskSegment;
    return ScanExecution.RunPartitionedAsync(
        "repeated-disk-try-get country/status",
        keys.Length,
        config.Parallelism,
        (start, count, checksum) =>
        {
          for (var i = start; i < start + count; i++)
          {
            var keyHashProvider = new ZoneTree.Hashers.KeyHashProvider<string>();
            if (diskSegment.TryGet(keys[i].Text, out var userId, ref keyHashProvider))
              checksum.Add(userId);
          }
        });
  }

  Task RunCountryStatusTryGetAsync(string name, CountryStatusKey[] keys) =>
      ScanExecution.RunPartitionedAsync(
          name,
          keys.Length,
          config.Parallelism,
          (start, count, checksum) =>
          {
            for (var i = start; i < start + count; i++)
            {
              if (CountryStatusIndex!.TryGet(keys[i].Text, out var userId))
                checksum.Add(userId);
            }
          });

  public Task RunCountryStatusSeekAsync(CountryStatusKey[] keys) =>
      RunCountryStatusIteratorSeekAsync("seek country/status", keys);

  public Task RunCountryStatusRepeatedSeekAsync(CountryStatusKey[] keys) =>
      RunCountryStatusIteratorSeekAsync("repeated-seek country/status", keys);

  public Task RunCountryStatusPrefixSeekAsync(CountryStatusKey[] prefixes) =>
      RunCountryStatusIteratorSeekAsync("prefix-seek country/status", prefixes);

  Task RunCountryStatusIteratorSeekAsync(string name, CountryStatusKey[] keys) =>
      ScanExecution.RunPartitionedAsync(
          name,
          keys.Length,
          config.Parallelism,
          (start, count, checksum) =>
          {
            if (config.ReuseIterator)
            {
              using var iterator = CreateStringIndexIterator(CountryStatusIndex!);
              for (var i = start; i < start + count; i++)
                SeekStringIndexValue(iterator, keys[i].Text, checksum);
              return;
            }

            for (var i = start; i < start + count; i++)
            {
              using var iterator = CreateStringIndexIterator(CountryStatusIndex!);
              SeekStringIndexValue(iterator, keys[i].Text, checksum);
            }
          });

  public Task RunCountryStatusScanAsync(CountryStatusQuery[] queries) =>
      ScanExecution.RunPartitionedAsync(
          "scan country/status",
          queries.Length,
          config.Parallelism,
          (start, count, checksum) =>
          {
            if (config.ReuseIterator)
            {
              using var iterator = CreateStringIndexIterator(CountryStatusIndex!);
              for (var i = start; i < start + count; i++)
              {
                var query = queries[i];
                var prefix = ProfileKeys.CountryStatusPrefix(query.Country, query.Status);
                ScanStringIndexValues(
                    iterator,
                    prefix,
                    key => key.StartsWith(prefix, StringComparison.Ordinal),
                    checksum);
              }
              return;
            }

            for (var i = start; i < start + count; i++)
            {
              var query = queries[i];
              var prefix = ProfileKeys.CountryStatusPrefix(query.Country, query.Status);
              ScanStringIndexValues(
                  CountryStatusIndex!,
                  prefix,
                  key => key.StartsWith(prefix, StringComparison.Ordinal),
                  checksum);
            }
          });

  public Task RunCountryStatusQueryAsync(CountryStatusQuery[] queries) =>
      ScanExecution.RunPartitionedAsync(
          "query country/status",
          queries.Length,
          config.Parallelism,
          (start, count, checksum) =>
          {
            for (var i = start; i < start + count; i++)
            {
              var query = queries[i];
              var prefix = ProfileKeys.CountryStatusPrefix(query.Country, query.Status);
              ScanStringIndexProfiles(
                  CountryStatusIndex!,
                  prefix,
                  key => key.StartsWith(prefix, StringComparison.Ordinal),
                  checksum);
            }
          });

  public Task RunCreatedAtScanAsync(CreatedAtQuery[] queries) =>
      ScanExecution.RunPartitionedAsync(
          "scan created-at",
          queries.Length,
          config.Parallelism,
          (start, count, checksum) =>
          {
            for (var i = start; i < start + count; i++)
            {
              var query = queries[i];
              var startKey = ProfileKeys.CreatedAt(query.FromUnixMs, 0);
              var endKey = ProfileKeys.CreatedAt(query.ToUnixMs, long.MaxValue);
              ScanStringIndexValues(
                  CreatedAtIndex!,
                  startKey,
                  key => string.CompareOrdinal(key, endKey) <= 0,
                  checksum);
            }
          });

  public Task RunCreatedAtQueryAsync(CreatedAtQuery[] queries) =>
      ScanExecution.RunPartitionedAsync(
          "query created-at",
          queries.Length,
          config.Parallelism,
          (start, count, checksum) =>
          {
            for (var i = start; i < start + count; i++)
            {
              var query = queries[i];
              var startKey = ProfileKeys.CreatedAt(query.FromUnixMs, 0);
              var endKey = ProfileKeys.CreatedAt(query.ToUnixMs, long.MaxValue);
              ScanStringIndexProfiles(
                  CreatedAtIndex!,
                  startKey,
                  key => string.CompareOrdinal(key, endKey) <= 0,
                  checksum);
            }
          });

  public Task RunTopReputationScanAsync() =>
      ScanExecution.RunPartitionedAsync(
          "scan top reputation",
          config.ScanCount,
          config.Parallelism,
          (_, count, checksum) =>
          {
            for (var i = 0; i < count; i++)
            {
              ScanStringIndexValues(
                  ReputationIndex!,
                  "",
                  _ => true,
                  checksum);
            }
          });

  public Task RunTopReputationQueryAsync() =>
      ScanExecution.RunPartitionedAsync(
          "query top reputation",
          config.ScanCount,
          config.Parallelism,
          (_, count, checksum) =>
          {
            for (var i = 0; i < count; i++)
            {
              ScanStringIndexProfiles(
                  ReputationIndex!,
                  "",
                  _ => true,
                  checksum);
            }
          });

  void ScanStringIndexValues(
      IZoneTree<string, long> index,
      string startKey,
      Func<string, bool> keepGoing,
      Checksum checksum)
  {
    using var iterator = CreateStringIndexIterator(index);
    ScanStringIndexValues(iterator, startKey, keepGoing, checksum);
  }

  /// <summary>
  /// Measures the bytes that each step of a short created-at scan allocates, on one thread. The first pass also
  /// brings the blocks and their entries into memory; the second pass shows what every later scan pays.
  /// </summary>
  public void ProfileScanAllocations(int scans)
  {
    var random = new Random(config.Seed);
    var starts = new string[scans];
    for (var i = 0; i < scans; i++)
      starts[i] = ProfileKeys.CreatedAt(Generator.Create(random.NextInt64(1, config.Profiles)).CreatedAtUnixMs, 0);

    for (var pass = 1; pass <= 2; pass++)
    {
      long create = 0, seek = 0, first = 0, rest = 0, dispose = 0;
      foreach (var start in starts)
      {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var iterator = CreateStringIndexIterator(CreatedAtIndex!);
        var created = GC.GetAllocatedBytesForCurrentThread();
        iterator.Seek(start);
        var sought = GC.GetAllocatedBytesForCurrentThread();
        iterator.Next();
        var firstRead = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1; i < config.Limit && iterator.Next(); i++)
        {
        }
        var read = GC.GetAllocatedBytesForCurrentThread();
        iterator.Dispose();
        var disposed = GC.GetAllocatedBytesForCurrentThread();
        create += created - before;
        seek += sought - created;
        first += firstRead - sought;
        rest += read - firstRead;
        dispose += disposed - read;
      }
      double perScan(long bytes) => bytes / (double)scans;
      Console.WriteLine(
          $"pass {pass}, bytes per scan: create={perScan(create):N0}; seek={perScan(seek):N0}; " +
          $"first next={perScan(first):N0}; next {config.Limit - 1} entries={perScan(rest):N0}; dispose={perScan(dispose):N0}");
    }
  }

  IZoneTreeIterator<string, long> CreateStringIndexIterator(
      IZoneTree<string, long> index) =>
      index.CreateIterator(new IteratorOptions
      {
        IteratorType = IteratorType.NoRefresh,
        ContributeToTheBlockCache = true,
        DiskSegmentPrefetchSize = config.PrefetchSize
      });

  void ScanStringIndexValues(
      IZoneTreeIterator<string, long> iterator,
      string startKey,
      Func<string, bool> keepGoing,
      Checksum checksum)
  {
    if (startKey.Length == 0)
      iterator.SeekFirst();
    else
      iterator.Seek(startKey);

    var count = 0;
    while (count < config.Limit && iterator.Next())
    {
      if (!keepGoing(iterator.CurrentKey))
        break;
      checksum.Add(iterator.CurrentValue);
      count++;
    }
  }

  static void SeekStringIndexValue(
      IZoneTreeIterator<string, long> iterator,
      string key,
      Checksum checksum)
  {
    iterator.Seek(key);
    if (iterator.Next())
      checksum.Add(iterator.CurrentValue);
  }

  void ScanStringIndexProfiles(
      IZoneTree<string, long> index,
      string startKey,
      Func<string, bool> keepGoing,
      Checksum checksum)
  {
    using var iterator = index.CreateIterator(new IteratorOptions
    {
      IteratorType = IteratorType.NoRefresh,
      ContributeToTheBlockCache = true,
      DiskSegmentPrefetchSize = config.PrefetchSize
    });

    if (startKey.Length == 0)
      iterator.SeekFirst();
    else
      iterator.Seek(startKey);

    var count = 0;
    while (count < config.Limit && iterator.Next())
    {
      if (!keepGoing(iterator.CurrentKey))
        break;
      if (TryGetProfile(iterator.CurrentValue, out var profile))
        checksum.Add(profile);
      count++;
    }
  }

  bool TryGetProfile(long userId, out UserProfile profile)
  {
    if (Profiles!.TryGet(userId, out var value))
    {
      profile = ProfileCodec.Deserialize(value);
      return true;
    }
    profile = null!;
    return false;
  }

  IZoneTree<long, string> OpenLongStringTree(string path)
  {
    return new ZoneTreeFactory<long, string>()
        .SetDataDirectory(path)
        .SetMutableSegmentMaxItemCount(config.MutableSegmentMaxItemCount)
        .SetLogLevel(LogLevel.Error)
        .SetComparer(new Int64ComparerAscending())
        .SetKeySerializer(new Int64Serializer())
        .SetValueSerializer(new Utf8StringSerializer())
        .Configure(options => options.BTreeLockMode = config.BTreeLockMode)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DefaultSparseArrayStepSize = config.SparseArrayStepSize;
          options.KeyCacheSize = config.KeyCacheSize;
          options.ValueCacheSize = config.ValueCacheSize;
          if (config.MaterializedEntryCacheSize >= 0)
            options.MaterializedEntryCacheSize = config.MaterializedEntryCacheSize;
        })
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.AsyncPrivate)
        .OpenOrCreate();
  }

  IZoneTree<string, long> OpenStringLongTree(string path)
  {
    return new ZoneTreeFactory<string, long>()
        .SetDataDirectory(path)
        .SetMutableSegmentMaxItemCount(config.MutableSegmentMaxItemCount)
        .SetLogLevel(LogLevel.Error)
        .SetComparer(new StringOrdinalComparerAscending())
        .SetKeySerializer(new Utf8StringSerializer())
        .SetValueSerializer(new Int64Serializer())
        .Configure(options => options.BTreeLockMode = config.BTreeLockMode)
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DefaultSparseArrayStepSize = config.SparseArrayStepSize;
          options.KeyCacheSize = config.KeyCacheSize;
          options.ValueCacheSize = config.ValueCacheSize;
          if (config.MaterializedEntryCacheSize >= 0)
            options.MaterializedEntryCacheSize = config.MaterializedEntryCacheSize;
        })
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.AsyncPrivate)
        .OpenOrCreate();
  }

  void AddMaintainer<TKey, TValue>(IZoneTree<TKey, TValue> tree)
  {
    var maintainer = tree.CreateMaintainer();
    maintainer.BlockCacheLifeTime = TimeSpan.FromMinutes(config.BlockCacheLifeTimeMinutes);
    maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromMinutes(config.BlockCacheLifeTimeMinutes / 2.0);
    Maintainers.Add(maintainer);
  }

  void SaveMetaData()
  {
    Profiles!.Maintenance.SaveMetaData();
    CountryStatusIndex!.Maintenance.SaveMetaData();
    CreatedAtIndex!.Maintenance.SaveMetaData();
    ReputationIndex!.Maintenance.SaveMetaData();
  }

  public async ValueTask DisposeAsync()
  {
    foreach (var maintainer in Maintainers)
      await maintainer.WaitForBackgroundThreadsAsync();
    foreach (var maintainer in Maintainers)
      maintainer.Dispose();

    Profiles?.Dispose();
    CountryStatusIndex?.Dispose();
    CreatedAtIndex?.Dispose();
    ReputationIndex?.Dispose();
  }

  static bool HasPersistedFiles(string path) =>
      Directory.Exists(path) &&
      Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();
}

sealed class RocksDbScanStore(ScanLabConfig config) : IScanStore
{
  static readonly Encoding Encoding = Encoding.UTF8;

  public ProfileGenerator Generator { get; } = new(config.Seed);

  RocksDb? Profiles;
  RocksDb? CountryStatusIndex;
  RocksDb? CreatedAtIndex;
  RocksDb? ReputationIndex;
  WriteOptions? WriteOptions;
  string RootPath => config.EngineDataDirectory;
  string ProfilesPath => Path.Combine(RootPath, "profiles");
  string CountryStatusPath => Path.Combine(RootPath, "country-status-index");
  string CreatedAtPath => Path.Combine(RootPath, "created-at-index");
  string ReputationPath => Path.Combine(RootPath, "reputation-index");

  public void Open()
  {
    Directory.CreateDirectory(RootPath);
    Profiles = OpenDb(ProfilesPath);
    CountryStatusIndex = OpenDb(CountryStatusPath);
    CreatedAtIndex = OpenDb(CreatedAtPath);
    ReputationIndex = OpenDb(ReputationPath);
    WriteOptions = new WriteOptions().SetSync(false);
  }

  public bool HasData() =>
      HasPersistedFiles(ProfilesPath) ||
      HasPersistedFiles(CountryStatusPath) ||
      HasPersistedFiles(CreatedAtPath) ||
      HasPersistedFiles(ReputationPath);

  public Task InsertAsync(int profiles)
  {
    Console.Write("insert indexes... ");
    var sw = Stopwatch.StartNew();
    for (var id = 1; id <= profiles; id++)
    {
      var profile = Generator.Create(id);
      var userId = UserIdValue(profile.UserId);
      Profiles!.Put(UserIdKey(profile.UserId), Bytes(ProfileCodec.Serialize(profile)), null, WriteOptions);
      CountryStatusIndex!.Put(Bytes(ProfileKeys.CountryStatus(profile)), userId, null, WriteOptions);
      CreatedAtIndex!.Put(Bytes(ProfileKeys.CreatedAt(profile)), userId, null, WriteOptions);
      ReputationIndex!.Put(Bytes(ProfileKeys.Reputation(profile)), userId, null, WriteOptions);
    }
    sw.Stop();
    Console.WriteLine($"{sw.Elapsed.TotalMilliseconds:N0} ms ({profiles / Math.Max(sw.Elapsed.TotalSeconds, 0.001):N0}/s)");
    return Task.CompletedTask;
  }

  public Task SettleAsync()
  {
    Console.Write("settle... ");
    var sw = Stopwatch.StartNew();
    Profiles!.CompactRange((byte[]?)null, null, null);
    CountryStatusIndex!.CompactRange((byte[]?)null, null, null);
    CreatedAtIndex!.CompactRange((byte[]?)null, null, null);
    ReputationIndex!.CompactRange((byte[]?)null, null, null);
    sw.Stop();
    Console.WriteLine($"{sw.Elapsed.TotalMilliseconds:N0} ms");
    return Task.CompletedTask;
  }

  public Task RunProfileReadAsync(ProfileReadPlan plan) =>
      ScanExecution.RunPartitionedAsync(
          $"read profiles ({config.ReadOrder.ToString().ToLowerInvariant()})",
          plan.UserIds.Length,
          config.Parallelism,
          (start, count, checksum) =>
          {
            for (var i = start; i < start + count; i++)
            {
              if (TryGetProfile(plan.UserIds[i], out var profile))
                checksum.Add(profile);
            }
          });

  public Task RunCountryStatusTryGetAsync(CountryStatusKey[] keys) =>
      RunCountryStatusTryGetAsync("try-get country/status", keys);

  public Task RunCountryStatusRepeatedTryGetAsync(CountryStatusKey[] keys) =>
      RunCountryStatusTryGetAsync("repeated-try-get country/status", keys);

  public Task RunCountryStatusRepeatedDiskTryGetAsync(CountryStatusKey[] keys) =>
      throw new NotSupportedException(
          "repeated-disk-try-get is available only for ZoneTree.");

  Task RunCountryStatusTryGetAsync(string name, CountryStatusKey[] keys) =>
      ScanExecution.RunPartitionedAsync(
          name,
          keys.Length,
          config.Parallelism,
          (start, count, checksum) =>
          {
            for (var i = start; i < start + count; i++)
            {
              var value = CountryStatusIndex!.Get(keys[i].Bytes, null, null);
              if (value != null)
                checksum.Add(ParseUserId(value));
            }
          });

  public Task RunCountryStatusSeekAsync(CountryStatusKey[] keys) =>
      RunCountryStatusIteratorSeekAsync("seek country/status", keys);

  public Task RunCountryStatusRepeatedSeekAsync(CountryStatusKey[] keys) =>
      RunCountryStatusIteratorSeekAsync("repeated-seek country/status", keys);

  public Task RunCountryStatusPrefixSeekAsync(CountryStatusKey[] prefixes) =>
      RunCountryStatusIteratorSeekAsync("prefix-seek country/status", prefixes);

  Task RunCountryStatusIteratorSeekAsync(string name, CountryStatusKey[] keys) =>
      ScanExecution.RunPartitionedAsync(
          name,
          keys.Length,
          config.Parallelism,
          (start, count, checksum) =>
          {
            if (config.ReuseIterator)
            {
              using var iterator = CountryStatusIndex!.NewIterator(null);
              for (var i = start; i < start + count; i++)
                SeekStringIndexValue(iterator, keys[i].Bytes, checksum);
              return;
            }

            for (var i = start; i < start + count; i++)
            {
              using var iterator = CountryStatusIndex!.NewIterator(null);
              SeekStringIndexValue(iterator, keys[i].Bytes, checksum);
            }
          });

  public Task RunCountryStatusScanAsync(CountryStatusQuery[] queries) =>
      ScanExecution.RunPartitionedAsync(
          "scan country/status",
          queries.Length,
          config.Parallelism,
          (start, count, checksum) =>
          {
            if (config.ReuseIterator)
            {
              using var iterator = CountryStatusIndex!.NewIterator(null);
              for (var i = start; i < start + count; i++)
              {
                var query = queries[i];
                var prefix = ProfileKeys.CountryStatusPrefix(query.Country, query.Status);
                ScanStringIndexValues(
                    iterator,
                    prefix,
                    key => key.StartsWith(prefix, StringComparison.Ordinal),
                    checksum);
              }
              return;
            }

            for (var i = start; i < start + count; i++)
            {
              var query = queries[i];
              var prefix = ProfileKeys.CountryStatusPrefix(query.Country, query.Status);
              ScanStringIndexValues(
                  CountryStatusIndex!,
                  prefix,
                  key => key.StartsWith(prefix, StringComparison.Ordinal),
                  checksum);
            }
          });

  public Task RunCountryStatusQueryAsync(CountryStatusQuery[] queries) =>
      ScanExecution.RunPartitionedAsync(
          "query country/status",
          queries.Length,
          config.Parallelism,
          (start, count, checksum) =>
          {
            for (var i = start; i < start + count; i++)
            {
              var query = queries[i];
              var prefix = ProfileKeys.CountryStatusPrefix(query.Country, query.Status);
              ScanStringIndexProfiles(
                  CountryStatusIndex!,
                  prefix,
                  key => key.StartsWith(prefix, StringComparison.Ordinal),
                  checksum);
            }
          });

  public Task RunCreatedAtScanAsync(CreatedAtQuery[] queries) =>
      ScanExecution.RunPartitionedAsync(
          "scan created-at",
          queries.Length,
          config.Parallelism,
          (start, count, checksum) =>
          {
            for (var i = start; i < start + count; i++)
            {
              var query = queries[i];
              var startKey = ProfileKeys.CreatedAt(query.FromUnixMs, 0);
              var endKey = ProfileKeys.CreatedAt(query.ToUnixMs, long.MaxValue);
              ScanStringIndexValues(
                  CreatedAtIndex!,
                  startKey,
                  key => string.CompareOrdinal(key, endKey) <= 0,
                  checksum);
            }
          });

  public Task RunCreatedAtQueryAsync(CreatedAtQuery[] queries) =>
      ScanExecution.RunPartitionedAsync(
          "query created-at",
          queries.Length,
          config.Parallelism,
          (start, count, checksum) =>
          {
            for (var i = start; i < start + count; i++)
            {
              var query = queries[i];
              var startKey = ProfileKeys.CreatedAt(query.FromUnixMs, 0);
              var endKey = ProfileKeys.CreatedAt(query.ToUnixMs, long.MaxValue);
              ScanStringIndexProfiles(
                  CreatedAtIndex!,
                  startKey,
                  key => string.CompareOrdinal(key, endKey) <= 0,
                  checksum);
            }
          });

  public Task RunTopReputationScanAsync() =>
      ScanExecution.RunPartitionedAsync(
          "scan top reputation",
          config.ScanCount,
          config.Parallelism,
          (_, count, checksum) =>
          {
            for (var i = 0; i < count; i++)
            {
              ScanStringIndexValues(
                  ReputationIndex!,
                  "",
                  _ => true,
                  checksum);
            }
          });

  public Task RunTopReputationQueryAsync() =>
      ScanExecution.RunPartitionedAsync(
          "query top reputation",
          config.ScanCount,
          config.Parallelism,
          (_, count, checksum) =>
          {
            for (var i = 0; i < count; i++)
            {
              ScanStringIndexProfiles(
                  ReputationIndex!,
                  "",
                  _ => true,
                  checksum);
            }
          });

  void ScanStringIndexValues(
      RocksDb index,
      string startKey,
      Func<string, bool> keepGoing,
      Checksum checksum)
  {
    using var iterator = index.NewIterator(null);
    ScanStringIndexValues(iterator, startKey, keepGoing, checksum);
  }

  void ScanStringIndexValues(
      Iterator iterator,
      string startKey,
      Func<string, bool> keepGoing,
      Checksum checksum)
  {
    if (startKey.Length == 0)
      iterator.SeekToFirst();
    else
      iterator.Seek(startKey);

    var count = 0;
    while (count < config.Limit && iterator.Valid())
    {
      var key = iterator.StringKey();
      if (!keepGoing(key))
        break;
      checksum.Add(ParseUserId(iterator.Value()));
      iterator.Next();
      count++;
    }
  }

  static void SeekStringIndexValue(
      Iterator iterator,
      byte[] key,
      Checksum checksum)
  {
    iterator.Seek(key);
    if (iterator.Valid())
      checksum.Add(ParseUserId(iterator.Value()));
  }

  void ScanStringIndexProfiles(
      RocksDb index,
      string startKey,
      Func<string, bool> keepGoing,
      Checksum checksum)
  {
    using var iterator = index.NewIterator(null);
    if (startKey.Length == 0)
      iterator.SeekToFirst();
    else
      iterator.Seek(startKey);

    var count = 0;
    while (count < config.Limit && iterator.Valid())
    {
      var key = iterator.StringKey();
      if (!keepGoing(key))
        break;
      if (TryGetProfile(ParseUserId(iterator.Value()), out var profile))
        checksum.Add(profile);
      iterator.Next();
      count++;
    }
  }

  bool TryGetProfile(long userId, out UserProfile profile)
  {
    var value = Profiles!.Get(UserIdKey(userId), null, null);
    if (value != null)
    {
      profile = ProfileCodec.Deserialize(Encoding.GetString(value));
      return true;
    }
    profile = null!;
    return false;
  }

  RocksDb OpenDb(string path)
  {
    Directory.CreateDirectory(path);
    var options = new DbOptions()
        .SetCreateIfMissing(true)
        .IncreaseParallelism(Environment.ProcessorCount)
        .SetWriteBufferSize(1024UL * 1024UL * 1024UL)
        .SetMaxWriteBufferNumber(4)
        .SetCompression(Compression.Zstd)
        .OptimizeLevelStyleCompaction(1024UL * 1024UL * 1024UL);
    return RocksDb.Open(options, path);
  }

  public ValueTask DisposeAsync()
  {
    Profiles?.Dispose();
    CountryStatusIndex?.Dispose();
    CreatedAtIndex?.Dispose();
    ReputationIndex?.Dispose();
    return ValueTask.CompletedTask;
  }

  static byte[] Bytes(string value) =>
      Encoding.GetBytes(value);

  static byte[] UserIdKey(long userId) =>
      UserIdValue(userId);

  static byte[] UserIdValue(long userId)
  {
    var bytes = new byte[sizeof(long)];
    BinaryPrimitives.WriteInt64BigEndian(bytes, userId);
    return bytes;
  }

  static long ParseUserId(byte[] value)
  {
    if (value.Length != sizeof(long))
      throw new FormatException($"Expected an 8-byte Int64 value, but received {value.Length} bytes.");
    return BinaryPrimitives.ReadInt64BigEndian(value);
  }

  static bool HasPersistedFiles(string path) =>
      Directory.Exists(path) &&
      Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();
}

static class ScanExecution
{
  public static async Task RunPartitionedAsync(
      string name,
      int operations,
      int parallelism,
      Action<int, int, Checksum> action)
  {
    Console.Write($"{name}... ");
    ThreadPool.GetMinThreads(
        out var minimumWorkerThreads,
        out var minimumCompletionPortThreads);
    if (minimumWorkerThreads < parallelism &&
        !ThreadPool.SetMinThreads(parallelism, minimumCompletionPortThreads))
    {
      throw new InvalidOperationException(
          $"Unable to configure {parallelism} ThreadPool worker threads.");
    }

    using var workersReady = new CountdownEvent(parallelism);
    using var startGate = new ManualResetEventSlim(false);
    var tasks = new Task<string>[parallelism];

    for (var worker = 0; worker < tasks.Length; worker++)
    {
      var index = worker;
      var (start, count) = GetPartition(operations, index, parallelism);
      tasks[index] = Task.Run(() =>
      {
        var checksum = new Checksum();
        workersReady.Signal();
        startGate.Wait();
        action(start, count, checksum);
        return checksum.Hex;
      });
    }

    workersReady.Wait();
    var allocatedBefore = GC.GetTotalAllocatedBytes();
    var gen0Before = GC.CollectionCount(0);
    var gen2Before = GC.CollectionCount(2);
    var sw = Stopwatch.StartNew();
    startGate.Set();
    var checksums = await Task.WhenAll(tasks);
    sw.Stop();
    var allocated = GC.GetTotalAllocatedBytes() - allocatedBefore;

    var checksum = new Checksum();
    foreach (var value in checksums)
      checksum.Add(value);
    Console.WriteLine(
        $"{sw.Elapsed.TotalMilliseconds:N0} ms ({operations / Math.Max(sw.Elapsed.TotalSeconds, 0.001):N0}/s); " +
        $"allocated={allocated / (1024.0 * 1024 * 1024):N2} GB; gc0={GC.CollectionCount(0) - gen0Before}; " +
        $"gc2={GC.CollectionCount(2) - gen2Before}; checksum={checksum.Hex}");
  }

  static (int Start, int Count) GetPartition(int total, int index, int partitions)
  {
    var baseCount = total / partitions;
    var remainder = total % partitions;
    var count = baseCount + (index < remainder ? 1 : 0);
    var start = index * baseCount + Math.Min(index, remainder);
    return (start, count);
  }
}

sealed record ScanPlan(
    ProfileReadPlan ProfileReads,
    CountryStatusKey[] CountryStatusKeys,
    CountryStatusKey[] RepeatedCountryStatusKeys,
    CountryStatusKey[] CountryStatusPrefixes,
    CountryStatusQuery[] CountryStatusQueries,
    CreatedAtQuery[] CreatedAtQueries)
{
  public static ScanPlan Create(ScanLabConfig config, ProfileGenerator generator)
  {
    var random = new Random(config.Seed);
    var createsIndexQueries = config.Mode is RunMode.Scan or RunMode.Query or RunMode.All;
    return new ScanPlan(
        CreateProfileReads(config),
        (config.Mode is RunMode.TryGet or RunMode.Seek) &&
            (config.Scan is ScanKind.CountryStatus or ScanKind.All)
            ? CreateCountryStatusKeys(config.ScanCount, config.Profiles, generator, random)
            : [],
        (config.Mode is RunMode.RepeatedTryGet or RunMode.RepeatedDiskTryGet or RunMode.RepeatedSeek) &&
            (config.Scan is ScanKind.CountryStatus or ScanKind.All)
            ? CreateRepeatedCountryStatusKeys(config.ScanCount, config.Profiles, generator, random)
            : [],
        config.Mode == RunMode.PrefixSeek &&
            (config.Scan is ScanKind.CountryStatus or ScanKind.All)
            ? CreateCountryStatusPrefixes(config.ScanCount, generator, random)
            : [],
        createsIndexQueries && (config.Scan is ScanKind.CountryStatus or ScanKind.All)
            ? CreateCountryStatusQueries(config.ScanCount, generator, random)
            : [],
        createsIndexQueries && (config.Scan is ScanKind.CreatedAt or ScanKind.All)
            ? CreateCreatedAtQueries(config.ScanCount, config.Profiles, generator, random, config.CreatedAtMode)
            : []);
  }

  static CountryStatusKey[] CreateCountryStatusKeys(
      int count,
      int maxId,
      ProfileGenerator generator,
      Random random)
  {
    var keys = new CountryStatusKey[count];
    for (var i = 0; i < keys.Length; i++)
    {
      var userId = random.NextInt64(1, maxId + 1L);
      var key = ProfileKeys.CountryStatus(generator.Create(userId));
      keys[i] = new CountryStatusKey(key, Encoding.UTF8.GetBytes(key));
    }
    return keys;
  }

  static CountryStatusKey[] CreateCountryStatusPrefixes(
      int count,
      ProfileGenerator generator,
      Random random)
  {
    var countries = generator.CountryCodes;
    var statuses = generator.StatusValues;
    var prefixes = new CountryStatusKey[count];
    for (var i = 0; i < prefixes.Length; i++)
    {
      var country = countries[random.Next(0, countries.Count)];
      var status = statuses[random.Next(0, statuses.Count)];
      var prefix = ProfileKeys.CountryStatusPrefix(country, status);
      prefixes[i] = new CountryStatusKey(prefix, Encoding.UTF8.GetBytes(prefix));
    }
    return prefixes;
  }

  static CountryStatusKey[] CreateRepeatedCountryStatusKeys(
      int count,
      int maxId,
      ProfileGenerator generator,
      Random random)
  {
    var expectedKeyCount = generator.CountryCodes.Count * generator.StatusValues.Count;
    var distinctKeys = new Dictionary<(string Country, string Status), CountryStatusKey>();
    for (var userId = 1L; userId <= maxId && distinctKeys.Count < expectedKeyCount; userId++)
    {
      var profile = generator.Create(userId);
      var group = (profile.Country, profile.Status);
      if (distinctKeys.ContainsKey(group))
        continue;

      var key = ProfileKeys.CountryStatus(profile);
      distinctKeys.Add(group, new CountryStatusKey(key, Encoding.UTF8.GetBytes(key)));
    }

    if (distinctKeys.Count != expectedKeyCount)
      throw new InvalidOperationException(
          $"Expected {expectedKeyCount} country/status groups, but found {distinctKeys.Count} in {maxId} profiles.");

    var keyPool = distinctKeys.Values
        .OrderBy(x => x.Text, StringComparer.Ordinal)
        .ToArray();
    var keys = new CountryStatusKey[count];
    for (var i = 0; i < keys.Length; i++)
      keys[i] = keyPool[random.Next(0, keyPool.Length)];
    return keys;
  }

  static ProfileReadPlan CreateProfileReads(ScanLabConfig config)
  {
    if (config.Mode is not RunMode.Read and not RunMode.All)
      return new ProfileReadPlan([]);

    var operationCount = checked(config.ScanCount * config.Limit);
    var userIds = new int[operationCount];
    if (config.ReadOrder == ProfileReadOrder.Sequential)
    {
      for (var i = 0; i < userIds.Length; i++)
        userIds[i] = i % config.Profiles + 1;
    }
    else if (config.ReadOrder == ProfileReadOrder.Clustered)
    {
      var random = new Random(config.Seed);
      var maxStart = Math.Max(1, config.Profiles - config.Limit + 1);
      for (var cluster = 0; cluster < config.ScanCount; cluster++)
      {
        var startId = (int)random.NextInt64(1, maxStart + 1L);
        var destinationIndex = cluster * config.Limit;
        for (var offset = 0; offset < config.Limit; offset++)
          userIds[destinationIndex + offset] = (startId + offset - 1) % config.Profiles + 1;
      }
    }
    else
    {
      var random = new Random(config.Seed);
      for (var i = 0; i < userIds.Length; i++)
        userIds[i] = (int)random.NextInt64(1, config.Profiles + 1L);
    }
    return new ProfileReadPlan(userIds);
  }

  static CountryStatusQuery[] CreateCountryStatusQueries(
      int count,
      ProfileGenerator generator,
      Random random)
  {
    var countries = generator.CountryCodes;
    var statuses = generator.StatusValues;
    var queries = new CountryStatusQuery[count];
    for (var i = 0; i < queries.Length; i++)
    {
      var country = countries[random.Next(0, countries.Count)];
      var status = statuses[random.Next(0, statuses.Count)];
      queries[i] = new CountryStatusQuery(country, status);
    }
    return queries;
  }

  static CreatedAtQuery[] CreateCreatedAtQueries(
      int count,
      int maxId,
      ProfileGenerator generator,
      Random random,
      CreatedAtQueryMode mode)
  {
    var queries = new CreatedAtQuery[count];
    if (mode == CreatedAtQueryMode.Fixed)
    {
      var startId = Math.Max(1, maxId / 2);
      var endId = Math.Min(maxId, startId + 5_000);
      var from = generator.Create(startId).CreatedAtUnixMs;
      var to = generator.Create(endId).CreatedAtUnixMs;
      for (var i = 0; i < queries.Length; i++)
        queries[i] = new CreatedAtQuery(from, to);
      return queries;
    }

    for (var i = 0; i < queries.Length; i++)
    {
      var startId = random.NextInt64(1, Math.Max(2, maxId - 5_000));
      var from = generator.Create(startId).CreatedAtUnixMs;
      var to = generator.Create(Math.Min(maxId, startId + 5_000)).CreatedAtUnixMs;
      queries[i] = new CreatedAtQuery(from, to);
    }
    return queries;
  }
}

sealed record ProfileReadPlan(int[] UserIds);

sealed record CountryStatusKey(string Text, byte[] Bytes);

sealed record CountryStatusQuery(string Country, string Status);

sealed record CreatedAtQuery(long FromUnixMs, long ToUnixMs);

sealed record UserProfile(
    long UserId,
    string Country,
    string Status,
    long CreatedAtUnixMs,
    int Reputation);

sealed class ProfileGenerator(int seed)
{
  static readonly string[] Countries =
  [
    "US", "DE", "GB", "FR", "NL", "SE", "NO", "DK",
    "FI", "ES", "IT", "PT", "PL", "CZ", "TR", "JP",
    "KR", "SG", "IN", "BR", "CA", "AU", "MX", "AR",
    "ZA", "IL", "IE", "CH", "AT", "BE", "RO", "GR"
  ];

  static readonly string[] Statuses = ["active", "suspended", "deleted", "pending"];

  const long BaseCreatedAt = 1_672_531_200_000;

  readonly ulong Seed = (uint)seed;

  public IReadOnlyList<string> CountryCodes => Countries;

  public IReadOnlyList<string> StatusValues => Statuses;

  public UserProfile Create(long userId)
  {
    var a = Mix((ulong)userId + Seed);
    var b = Mix(a + 0x9E3779B97F4A7C15UL);
    var country = Countries[(int)(a % (ulong)Countries.Length)];
    var status = Statuses[(int)((a >> 8) % (ulong)Statuses.Length)];
    var createdAt = BaseCreatedAt + userId * 1_000 + (long)(a % 997);
    var reputation = (int)(b % 1_000_000);
    return new UserProfile(userId, country, status, createdAt, reputation);
  }

  static ulong Mix(ulong value)
  {
    value += 0x9E3779B97F4A7C15UL;
    value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
    value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
    return value ^ (value >> 31);
  }
}

static class ProfileKeys
{
  const int MaxReputation = 1_000_000;

  public static string CountryStatus(UserProfile profile) =>
      $"{profile.Country}|{profile.Status}|{profile.UserId:D20}";

  public static string CountryStatusPrefix(string country, string status) =>
      $"{country}|{status}|";

  public static string CreatedAt(UserProfile profile) =>
      CreatedAt(profile.CreatedAtUnixMs, profile.UserId);

  public static string CreatedAt(long createdAtUnixMs, long userId) =>
      $"{createdAtUnixMs:D20}|{userId:D20}";

  public static string Reputation(UserProfile profile) =>
      $"{MaxReputation - profile.Reputation:D10}|{profile.UserId:D20}";
}

sealed class Checksum
{
  ulong Value = 14_695_981_039_346_656_037UL;

  public string Hex => Value.ToString("X16");

  public void Add(long value)
  {
    unchecked
    {
      Value ^= (ulong)value;
      Value *= 1_099_511_628_211UL;
    }
  }

  public void Add(string value)
  {
    foreach (var ch in value)
      Add(ch);
  }

  public void Add(UserProfile profile)
  {
    Add(profile.UserId);
    Add(profile.Country);
    Add(profile.Status);
    Add(profile.CreatedAtUnixMs);
    Add(profile.Reputation);
  }
}

static class ProfileCodec
{
  public static string Serialize(UserProfile profile) =>
      string.Join('|',
          profile.UserId,
          profile.Country,
          profile.Status,
          profile.CreatedAtUnixMs,
          profile.Reputation);

  public static UserProfile Deserialize(string value)
  {
    var parts = value.Split('|');
    if (parts.Length != 5)
      throw new FormatException($"Invalid profile payload: {value}");
    return new UserProfile(
        long.Parse(parts[0]),
        parts[1],
        parts[2],
        long.Parse(parts[3]),
        int.Parse(parts[4]));
  }
}

sealed record ScanLabConfig
{
  public EngineKind Engine { get; init; } = EngineKind.ZoneTree;
  public RunMode Mode { get; init; } = RunMode.Scan;
  public ScanKind Scan { get; init; } = ScanKind.CountryStatus;
  public int Profiles { get; init; } = 1_000_000;
  public int ScanCount { get; init; } = -1;
  public int Limit { get; init; } = 100;
  public int Parallelism { get; init; } = 16;
  public string DataDirectory { get; init; } = "data";
  public string EngineDataDirectory =>
      Path.Combine(
          DataDirectory,
          Engine switch
          {
            EngineKind.ZoneTree => "zonetree",
            EngineKind.RocksDb => "rocksdb",
            _ => throw new ArgumentOutOfRangeException(nameof(Engine))
          });
  public bool Reset { get; init; }
  public bool ReuseIterator { get; init; }
  public int Seed { get; init; } = 570123434;
  public int MutableSegmentMaxItemCount { get; init; } = 250_000;
  public int SparseArrayStepSize { get; init; } = 16;
  public int KeyCacheSize { get; init; } = 1024;
  public int ValueCacheSize { get; init; } = 1024;
  // Negative keeps the library default; zero disables the cache.
  public int MaterializedEntryCacheSize { get; init; } = -1;
  public int PrefetchSize { get; init; } = 16;
  public BTreeLockMode BTreeLockMode { get; init; } = BTreeLockMode.NodeLevelMonitor;
  public CreatedAtQueryMode CreatedAtMode { get; init; } = CreatedAtQueryMode.Random;
  public ProfileReadOrder ReadOrder { get; init; } = ProfileReadOrder.Random;
  public double BlockCacheLifeTimeMinutes { get; init; } = 1222;

  public ScanLabConfig WithDefaults() =>
      this with
      {
        ScanCount = ScanCount < 0 ? (Profiles <= 10_000 ? Profiles : Math.Max(1, Profiles / 4)) : ScanCount
      };

  public static ScanLabConfig Parse(string[] args)
  {
    var config = new ScanLabConfig();
    for (var i = 0; i < args.Length; i++)
    {
      var arg = args[i];
      string next()
      {
        if (++i >= args.Length)
          throw new ArgumentException($"Missing value for {arg}.");
        return args[i];
      }

      config = arg switch
      {
        "--engine" => config with { Engine = ParseEngine(next()) },
        "--mode" => config with { Mode = ParseMode(next()) },
        "--scan" => config with { Scan = ParseScan(next()) },
        "--profiles" => config with { Profiles = ParseCount(next()) },
        "--scan-count" => config with { ScanCount = ParseCount(next()) },
        "--limit" => config with { Limit = ParseCount(next()) },
        "--parallelism" => config with { Parallelism = ParseCount(next()) },
        "--data" => config with { DataDirectory = next() },
        "--reset" => config with { Reset = true },
        "--reuse-iterator" => config with { ReuseIterator = true },
        "--seed" => config with { Seed = int.Parse(next()) },
        "--mutable-max" => config with { MutableSegmentMaxItemCount = ParseCount(next()) },
        "--sparse-step" => config with { SparseArrayStepSize = ParseCount(next()) },
        "--key-cache" => config with { KeyCacheSize = ParseCount(next()) },
        "--value-cache" => config with { ValueCacheSize = ParseCount(next()) },
        "--materialized-cache" => config with { MaterializedEntryCacheSize = ParseCount(next()) },
        "--prefetch" => config with { PrefetchSize = ParseCount(next()) },
        "--btree-lock" => config with { BTreeLockMode = ParseBTreeLockMode(next()) },
        "--created-at" => config with { CreatedAtMode = ParseCreatedAtMode(next()) },
        "--read-order" => config with { ReadOrder = ParseReadOrder(next()) },
        "--block-cache-minutes" => config with { BlockCacheLifeTimeMinutes = double.Parse(next()) },
        _ => throw new ArgumentException($"Unknown argument: {arg}")
      };
    }

    if (config.Profiles <= 0)
      throw new ArgumentOutOfRangeException(nameof(config.Profiles));
    if (config.Limit <= 0)
      throw new ArgumentOutOfRangeException(nameof(config.Limit));
    if (config.Parallelism <= 0)
      throw new ArgumentOutOfRangeException(nameof(config.Parallelism));
    if (config.PrefetchSize < 0)
      throw new ArgumentOutOfRangeException(nameof(config.PrefetchSize));
    return config;
  }

  static EngineKind ParseEngine(string value) =>
      value.ToLowerInvariant() switch
      {
        "zonetree" => EngineKind.ZoneTree,
        "rocksdb" => EngineKind.RocksDb,
        _ => throw new ArgumentException($"Unknown engine: {value}")
      };

  static BTreeLockMode ParseBTreeLockMode(string value) =>
      value.ToLowerInvariant() switch
      {
        "no-lock" or "nolock" => BTreeLockMode.NoLock,
        "node-monitor" or "nodelevelmonitor" => BTreeLockMode.NodeLevelMonitor,
        "node-reader-writer" or "nodelevelreaderwriter" => BTreeLockMode.NodeLevelReaderWriter,
        "top-monitor" or "toplevelmonitor" => BTreeLockMode.TopLevelMonitor,
        "top-reader-writer" or "toplevelreaderwriter" => BTreeLockMode.TopLevelReaderWriter,
        _ => throw new ArgumentException($"Unknown BTree lock mode: {value}")
      };

  static RunMode ParseMode(string value) =>
      value.ToLowerInvariant() switch
      {
        "insert" => RunMode.Insert,
        "read" => RunMode.Read,
        "try-get" or "tryget" or "get" => RunMode.TryGet,
        "repeated-try-get" or "repeatedtryget" => RunMode.RepeatedTryGet,
        "repeated-disk-try-get" or "repeateddisktryget" => RunMode.RepeatedDiskTryGet,
        "seek" or "exact-seek" => RunMode.Seek,
        "repeated-seek" or "repeatedseek" => RunMode.RepeatedSeek,
        "prefix-seek" or "prefixseek" => RunMode.PrefixSeek,
        "scan" => RunMode.Scan,
        "query" => RunMode.Query,
        "all" => RunMode.All,
        "alloc" => RunMode.Alloc,
        _ => throw new ArgumentException($"Unknown mode: {value}")
      };

  static ProfileReadOrder ParseReadOrder(string value) =>
      value.ToLowerInvariant() switch
      {
        "random" => ProfileReadOrder.Random,
        "clustered" => ProfileReadOrder.Clustered,
        "sequential" => ProfileReadOrder.Sequential,
        _ => throw new ArgumentException($"Unknown profile read order: {value}")
      };

  static CreatedAtQueryMode ParseCreatedAtMode(string value) =>
      value.ToLowerInvariant() switch
      {
        "random" => CreatedAtQueryMode.Random,
        "fixed" => CreatedAtQueryMode.Fixed,
        _ => throw new ArgumentException($"Unknown created-at mode: {value}")
      };

  static ScanKind ParseScan(string value) =>
      value.ToLowerInvariant() switch
      {
        "country-status" => ScanKind.CountryStatus,
        "created-at" => ScanKind.CreatedAt,
        "top-reputation" => ScanKind.TopReputation,
        "all" => ScanKind.All,
        _ => throw new ArgumentException($"Unknown scan: {value}")
      };

  static int ParseCount(string value)
  {
    value = value.Replace("_", "", StringComparison.Ordinal).Trim();
    var multiplier = 1;
    if (value.EndsWith("K", StringComparison.OrdinalIgnoreCase))
    {
      multiplier = 1_000;
      value = value[..^1];
    }
    else if (value.EndsWith("M", StringComparison.OrdinalIgnoreCase))
    {
      multiplier = 1_000_000;
      value = value[..^1];
    }
    return checked((int)(double.Parse(value) * multiplier));
  }
}

enum EngineKind
{
  ZoneTree,
  RocksDb
}

enum RunMode
{
  Insert,
  Read,
  TryGet,
  RepeatedTryGet,
  RepeatedDiskTryGet,
  Seek,
  RepeatedSeek,
  PrefixSeek,
  Scan,
  Query,
  Alloc,
  All
}

enum ProfileReadOrder
{
  Random,
  Clustered,
  Sequential
}

enum CreatedAtQueryMode
{
  Random,
  Fixed
}

enum ScanKind
{
  CountryStatus,
  CreatedAt,
  TopReputation,
  All
}
