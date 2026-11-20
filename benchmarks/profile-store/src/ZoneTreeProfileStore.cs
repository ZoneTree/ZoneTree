using System.Globalization;
using ZoneTree;
using ZoneTree.Comparers;
using ZoneTree.Logging;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ProfileStore.Benchmark;

public sealed class ZoneTreeProfileStore : IProfileStoreEngine, IProfileStoreEngineWorker
{
  string RootDirectory = "";
  IZoneTree<long, string>? Profiles;
  IZoneTree<string, long>? EmailIndex;
  IZoneTree<string, long>? CountryStatusIndex;
  IZoneTree<string, long>? CreatedAtIndex;
  IZoneTree<string, long>? ReputationIndex;
  readonly List<IMaintainer> Maintainers = [];
  readonly List<Action> DetachMaintenanceHandlers = [];
  int MutableSegmentMaxItemCount;
  int SparseArrayStepSize;
  int KeyCacheSize;
  int ValueCacheSize;
  int IteratorPrefetchSize;
  TimeSpan BlockCacheLifetime;

  public string Name => "ZoneTree";

  public string DurabilityDescription =>
      $"AsyncPrivate WAL default; MutableSegmentMaxItemCount={MutableSegmentMaxItemCount.ToString(CultureInfo.InvariantCulture)}; SparseArrayStepSize={SparseArrayStepSize.ToString(CultureInfo.InvariantCulture)}; KeyCacheSize={KeyCacheSize.ToString(CultureInfo.InvariantCulture)}; ValueCacheSize={ValueCacheSize.ToString(CultureInfo.InvariantCulture)}; IteratorPrefetchSize={IteratorPrefetchSize.ToString(CultureInfo.InvariantCulture)}; BlockCacheLifetime={BlockCacheLifetime.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes; application-managed secondary indexes; background maintainers enabled.";

  public bool RequiresReadStabilization => true;

  public Task InitializeAsync(BenchmarkConfig config, bool reset, CancellationToken ct)
  {
    RootDirectory = Path.Combine(config.DataDirectory, "zonetree");
    MutableSegmentMaxItemCount = config.ZoneTreeMutableSegmentMaxItemCount;
    SparseArrayStepSize = config.ZoneTreeSparseArrayStepSize;
    KeyCacheSize = config.ZoneTreeKeyCacheSize;
    ValueCacheSize = config.ZoneTreeValueCacheSize;
    IteratorPrefetchSize = config.ZoneTreeIteratorPrefetchSize;
    BlockCacheLifetime = TimeSpan.FromMinutes(config.ZoneTreeBlockCacheLifetimeMinutes);
    if (reset && Directory.Exists(RootDirectory))
      Directory.Delete(RootDirectory, true);
    Directory.CreateDirectory(RootDirectory);

    Profiles = OpenLongStringTree(Path.Combine(RootDirectory, "profiles"), MutableSegmentMaxItemCount, SparseArrayStepSize, KeyCacheSize, ValueCacheSize);
    EmailIndex = OpenStringLongTree(Path.Combine(RootDirectory, "email-index"), MutableSegmentMaxItemCount, SparseArrayStepSize, KeyCacheSize, ValueCacheSize);
    CountryStatusIndex = OpenStringLongTree(Path.Combine(RootDirectory, "country-status-index"), MutableSegmentMaxItemCount, SparseArrayStepSize, KeyCacheSize, ValueCacheSize);
    CreatedAtIndex = OpenStringLongTree(Path.Combine(RootDirectory, "created-at-index"), MutableSegmentMaxItemCount, SparseArrayStepSize, KeyCacheSize, ValueCacheSize);
    ReputationIndex = OpenStringLongTree(Path.Combine(RootDirectory, "reputation-index"), MutableSegmentMaxItemCount, SparseArrayStepSize, KeyCacheSize, ValueCacheSize);
    DetachMaintainerHandlers();
    Maintainers.Clear();
    AddMaintainer(Profiles);
    AddMaintainer(EmailIndex);
    AddMaintainer(CountryStatusIndex);
    AddMaintainer(CreatedAtIndex);
    AddMaintainer(ReputationIndex);
    return Task.CompletedTask;
  }

  public Task<IProfileStoreEngineWorker> CreateWorkerAsync(CancellationToken ct) =>
      Task.FromResult<IProfileStoreEngineWorker>(new Worker(this));

  public Task InsertBatchAsync(IReadOnlyList<UserProfile> profiles, CancellationToken ct)
  {
    foreach (var profile in profiles)
    {
      ct.ThrowIfCancellationRequested();
      Profiles!.Upsert(profile.UserId, ProfileCodec.Serialize(profile));
      EmailIndex!.Upsert(profile.Email, profile.UserId);
      CountryStatusIndex!.Upsert(ProfileKeys.CountryStatus(profile), profile.UserId);
      CreatedAtIndex!.Upsert(ProfileKeys.CreatedAt(profile), profile.UserId);
      ReputationIndex!.Upsert(ProfileKeys.Reputation(profile), profile.UserId);
    }
    return Task.CompletedTask;
  }

  public Task<UserProfile?> GetByUserIdAsync(long userId, CancellationToken ct)
  {
    return Task.FromResult(TryGetProfile(userId, out var profile) ? profile : null);
  }

  public Task<UserProfile?> GetByEmailAsync(string email, CancellationToken ct)
  {
    if (!EmailIndex!.TryGet(email, out var userId))
      return Task.FromResult<UserProfile?>(null);
    return GetByUserIdAsync(userId, ct);
  }

  public Task<IReadOnlyList<UserProfile>> QueryCountryStatusAsync(
      string country,
      string status,
      int limit,
      CancellationToken ct)
  {
    var prefix = ProfileKeys.CountryStatusPrefix(country, status);
    return Task.FromResult(ScanStringIndex(CountryStatusIndex!, prefix, key => key.StartsWith(prefix, StringComparison.Ordinal), limit, ct));
  }

  public Task<IReadOnlyList<long>> ScanCountryStatusIndexAsync(
      string country,
      string status,
      int limit,
      CancellationToken ct)
  {
    var prefix = ProfileKeys.CountryStatusPrefix(country, status);
    return Task.FromResult(ScanStringIndexValues(CountryStatusIndex!, prefix, key => key.StartsWith(prefix, StringComparison.Ordinal), limit, ct));
  }

  public Task<IReadOnlyList<UserProfile>> QueryCreatedAtRangeAsync(
      long fromUnixMs,
      long toUnixMs,
      int limit,
      CancellationToken ct)
  {
    var start = ProfileKeys.CreatedAt(fromUnixMs, 0);
    var end = ProfileKeys.CreatedAt(toUnixMs, long.MaxValue);
    return Task.FromResult(ScanStringIndex(CreatedAtIndex!, start, key => string.CompareOrdinal(key, end) <= 0, limit, ct));
  }

  public Task<IReadOnlyList<long>> ScanCreatedAtRangeIndexAsync(
      long fromUnixMs,
      long toUnixMs,
      int limit,
      CancellationToken ct)
  {
    var start = ProfileKeys.CreatedAt(fromUnixMs, 0);
    var end = ProfileKeys.CreatedAt(toUnixMs, long.MaxValue);
    return Task.FromResult(ScanStringIndexValues(CreatedAtIndex!, start, key => string.CompareOrdinal(key, end) <= 0, limit, ct));
  }

  public Task<IReadOnlyList<UserProfile>> QueryTopReputationAsync(int limit, CancellationToken ct)
  {
    return Task.FromResult(ScanStringIndex(ReputationIndex!, "", _ => true, limit, ct));
  }

  public Task<IReadOnlyList<long>> ScanTopReputationIndexAsync(int limit, CancellationToken ct)
  {
    return Task.FromResult(ScanStringIndexValues(ReputationIndex!, "", _ => true, limit, ct));
  }

  public async Task UpdateBatchAsync(IReadOnlyList<UserProfile> profiles, CancellationToken ct)
  {
    foreach (var profile in profiles)
    {
      ct.ThrowIfCancellationRequested();
      var old = await GetByUserIdAsync(profile.UserId, ct)
          ?? throw new InvalidOperationException($"Missing profile {profile.UserId}");

      CountryStatusIndex!.ForceDelete(ProfileKeys.CountryStatus(old));
      ReputationIndex!.ForceDelete(ProfileKeys.Reputation(old));

      Profiles!.Upsert(profile.UserId, ProfileCodec.Serialize(profile));
      CountryStatusIndex.Upsert(ProfileKeys.CountryStatus(profile), profile.UserId);
      ReputationIndex.Upsert(ProfileKeys.Reputation(profile), profile.UserId);
    }
  }

  public Task StabilizeForReadMeasurementsAsync(CancellationToken ct) => SettleAsync(ct);

  public async Task SettleAsync(CancellationToken ct)
  {
    foreach (var maintainer in Maintainers)
    {
      ct.ThrowIfCancellationRequested();
      maintainer.EvictToDisk();
    }
    foreach (var maintainer in Maintainers)
    {
      ct.ThrowIfCancellationRequested();
      await maintainer.WaitForBackgroundThreadsAsync();
    }
    SaveMetadata();
  }

  public Task<long> CountProfilesAsync(CancellationToken ct)
  {
    return Task.FromResult(Profiles!.Count());
  }

  public Task<long> GetStorageSizeBytesAsync(CancellationToken ct)
  {
    return Task.FromResult(Directory.Exists(RootDirectory) ? GetDirectorySize(RootDirectory) : 0L);
  }

  public async ValueTask DisposeAsync()
  {
    foreach (var maintainer in Maintainers)
    {
      await maintainer.WaitForBackgroundThreadsAsync();
    }
    DetachMaintainerHandlers();
    foreach (var maintainer in Maintainers)
      maintainer.Dispose();
    Maintainers.Clear();

    Profiles?.Dispose();
    EmailIndex?.Dispose();
    CountryStatusIndex?.Dispose();
    CreatedAtIndex?.Dispose();
    ReputationIndex?.Dispose();
  }

  static IZoneTree<long, string> OpenLongStringTree(
      string path,
      int mutableSegmentMaxItemCount,
      int sparseArrayStepSize,
      int keyCacheSize,
      int valueCacheSize)
  {
    return new ZoneTreeFactory<long, string>()
        .SetDataDirectory(path)
        .SetMutableSegmentMaxItemCount(mutableSegmentMaxItemCount)
        .SetLogLevel(ZoneTreeLogLevel.Error)
        .SetComparer(new Int64ComparerAscending())
        .SetKeySerializer(new Int64Serializer())
        .SetValueSerializer(new Utf8StringSerializer())
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DefaultSparseArrayStepSize = sparseArrayStepSize;
          options.KeyCacheSize = keyCacheSize;
          options.ValueCacheSize = valueCacheSize;
        })
        .ConfigureWriteAheadLogOptions(options => options.Mode = WriteAheadLogMode.AsyncPrivate)
        .OpenOrCreate();
  }

  static IZoneTree<string, long> OpenStringLongTree(
      string path,
      int mutableSegmentMaxItemCount,
      int sparseArrayStepSize,
      int keyCacheSize,
      int valueCacheSize)
  {
    return new ZoneTreeFactory<string, long>()
        .SetDataDirectory(path)
        .SetMutableSegmentMaxItemCount(mutableSegmentMaxItemCount)
        .SetLogLevel(ZoneTreeLogLevel.Error)
        .SetComparer(new StringOrdinalComparerAscending())
        .SetKeySerializer(new Utf8StringSerializer())
        .SetValueSerializer(new Int64Serializer())
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DefaultSparseArrayStepSize = sparseArrayStepSize;
          options.KeyCacheSize = keyCacheSize;
          options.ValueCacheSize = valueCacheSize;
        })
        .ConfigureWriteAheadLogOptions(options => options.Mode = WriteAheadLogMode.AsyncPrivate)
        .OpenOrCreate();
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

  IReadOnlyList<UserProfile> ScanStringIndex(
      IZoneTree<string, long> index,
      string startKey,
      Func<string, bool> keepGoing,
      int limit,
      CancellationToken ct)
  {
    var results = new List<UserProfile>(limit);
    using var iterator = CreateIndexIterator(index);
    if (startKey.Length == 0)
      iterator.SeekFirst();
    else
      iterator.Seek(startKey);

    while (results.Count < limit && iterator.Next())
    {
      ct.ThrowIfCancellationRequested();
      if (!keepGoing(iterator.CurrentKey))
        break;
      if (TryGetProfile(iterator.CurrentValue, out var profile))
        results.Add(profile);
    }
    return results;
  }

  IReadOnlyList<long> ScanStringIndexValues(
      IZoneTree<string, long> index,
      string startKey,
      Func<string, bool> keepGoing,
      int limit,
      CancellationToken ct)
  {
    var results = new List<long>(limit);
    using var iterator = CreateIndexIterator(index);
    if (startKey.Length == 0)
      iterator.SeekFirst();
    else
      iterator.Seek(startKey);

    while (results.Count < limit && iterator.Next())
    {
      ct.ThrowIfCancellationRequested();
      if (!keepGoing(iterator.CurrentKey))
        break;
      results.Add(iterator.CurrentValue);
    }
    return results;
  }

  void SaveMetadata()
  {
    Profiles!.Maintenance.SaveMetadata();
    EmailIndex!.Maintenance.SaveMetadata();
    CountryStatusIndex!.Maintenance.SaveMetadata();
    CreatedAtIndex!.Maintenance.SaveMetadata();
    ReputationIndex!.Maintenance.SaveMetadata();
  }

  IZoneTreeIterator<string, long> CreateIndexIterator(IZoneTree<string, long> index)
  {
    return index.CreateIterator(new IteratorOptions
    {
      IteratorType = IteratorType.NoRefresh,
      ContributeToTheBlockCache = true,
      DiskSegmentPrefetchSize = IteratorPrefetchSize
    });
  }

  void AddMaintainer<TKey, TValue>(IZoneTree<TKey, TValue> tree)
  {
    var maintainer = tree.CreateMaintainer();
    maintainer.BlockCacheLifetime = BlockCacheLifetime;
    maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromMilliseconds(BlockCacheLifetime.TotalMilliseconds / 2);

    void onDiskSegmentActivated(
      IZoneTreeMaintenance<TKey, TValue> maintenance,
      ZoneTree.Segments.IDiskSegment<TKey, TValue> _,
      bool isBottomSegment)
    {
      if (maintenance.IsBottomSegmentsMerging ||
          maintenance.BottomSegments.Count < 2)
        return;
      maintainer.StartBottomSegmentsMerge();
    }
    tree.Maintenance.OnDiskSegmentActivated += onDiskSegmentActivated;
    DetachMaintenanceHandlers.Add(() =>
        tree.Maintenance.OnDiskSegmentActivated -= onDiskSegmentActivated);
    Maintainers.Add(maintainer);
  }

  void DetachMaintainerHandlers()
  {
    foreach (var detach in DetachMaintenanceHandlers)
      detach();
    DetachMaintenanceHandlers.Clear();
  }

  static long GetDirectorySize(string path)
  {
    return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .Sum(file => new FileInfo(file).Length);
  }

  sealed class Worker(ZoneTreeProfileStore store) : IProfileStoreEngineWorker
  {
    public Task InsertBatchAsync(IReadOnlyList<UserProfile> profiles, CancellationToken ct) =>
        store.InsertBatchAsync(profiles, ct);

    public Task<UserProfile?> GetByUserIdAsync(long userId, CancellationToken ct) =>
        store.GetByUserIdAsync(userId, ct);

    public Task<UserProfile?> GetByEmailAsync(string email, CancellationToken ct) =>
        store.GetByEmailAsync(email, ct);

    public Task<IReadOnlyList<UserProfile>> QueryCountryStatusAsync(
        string country,
        string status,
        int limit,
        CancellationToken ct) =>
        store.QueryCountryStatusAsync(country, status, limit, ct);

    public Task<IReadOnlyList<long>> ScanCountryStatusIndexAsync(
        string country,
        string status,
        int limit,
        CancellationToken ct) =>
        store.ScanCountryStatusIndexAsync(country, status, limit, ct);

    public Task<IReadOnlyList<UserProfile>> QueryCreatedAtRangeAsync(
        long fromUnixMs,
        long toUnixMs,
        int limit,
        CancellationToken ct) =>
        store.QueryCreatedAtRangeAsync(fromUnixMs, toUnixMs, limit, ct);

    public Task<IReadOnlyList<long>> ScanCreatedAtRangeIndexAsync(
        long fromUnixMs,
        long toUnixMs,
        int limit,
        CancellationToken ct) =>
        store.ScanCreatedAtRangeIndexAsync(fromUnixMs, toUnixMs, limit, ct);

    public Task<IReadOnlyList<UserProfile>> QueryTopReputationAsync(int limit, CancellationToken ct) =>
        store.QueryTopReputationAsync(limit, ct);

    public Task<IReadOnlyList<long>> ScanTopReputationIndexAsync(int limit, CancellationToken ct) =>
        store.ScanTopReputationIndexAsync(limit, ct);

    public Task UpdateBatchAsync(IReadOnlyList<UserProfile> profiles, CancellationToken ct) =>
        store.UpdateBatchAsync(profiles, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}
