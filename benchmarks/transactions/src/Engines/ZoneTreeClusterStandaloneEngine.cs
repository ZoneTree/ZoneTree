using System.Globalization;
using ZoneTree;
using ZoneTree.Cluster;
using ZoneTree.Options;
using ZoneTree.Segments;
using ZoneTree.Transactional;
using ZoneTree.Transactional.Versioning;

namespace Transactions.Benchmark.Engines;

/// <summary>
/// Runs the scenarios through a standalone column of a ZoneTreeCluster: the MVCC engine of
/// <see cref="ZoneTreeEngine"/> behind the cluster API, with the same storage configuration.
/// The difference between the two measures the cluster layer.
/// </summary>
public sealed class ZoneTreeClusterStandaloneEngine : ITransactionEngine
{
  readonly IZoneTreeClusterHost Cluster;
  readonly IStandaloneColumn<long, byte[]> Column;
  readonly IMaintainer Maintainer;
  readonly IZoneTreeMaintenance<VersionedKey<long>, VersionedValue<byte[]>> Storage;
  readonly TransactionIsolationLevel Isolation;
  readonly bool Durable;

  public IReadOnlyList<string> Settings { get; }

  public ZoneTreeClusterStandaloneEngine(EngineOpenOptions options, TransactionIsolationLevel isolation, WriteAheadLogMode walMode)
  {
    var tuning = options.Tuning;
    Isolation = isolation;
    Durable = options.Durability == DurabilityMode.PerCommit;
    Cluster = new ZoneTreeClusterFactory().OpenOrCreateInProcess(new ZoneTreeClusterOptions
    {
      DataDirectory = options.Directory,
      // The built-in encodings are the comparer and serializers ZoneTreeEngine uses too.
      Columns =
      [
        new StandaloneColumnDefinition<long, byte[]>("records")
        {
          ConfigureStorage = factory => ZoneTreeEngine.ConfigureStorage(factory, options, walMode)
        }
      ]
    });
    Column = Cluster.GetStandaloneColumn<long, byte[]>("records");
    var tree = Cluster.GetStandaloneColumnStorage<long, byte[]>("records");
    var effectiveWalMode = tree.Maintenance.ZoneTree.Maintenance.CloneOptions().WriteAheadLogOptions.WriteAheadLogMode;
    Maintainer = Cluster.CreateColumnMaintainer("records");
    Maintainer.BlockCacheLifeTime = TimeSpan.FromMinutes(1);
    Maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromSeconds(30);
    Storage = tree.Maintenance.ZoneTree.Maintenance;
    Storage.OnDiskSegmentActivated += StartBottomMerge;
    Settings =
    [
      "Cluster API: one standalone long/byte[] column over the MVCC engine of the zonetree-mvcc engines",
      $"Isolation: {isolation}",
      $"WAL: {effectiveWalMode}; compression disabled",
      Durable
          ? "TryCommitDurableAsync syncs the WAL before publishing each commit"
          : "Ordinary commits do not request a disk sync",
      string.Create(CultureInfo.InvariantCulture, $"Mutable segment: {tuning.ZoneTreeMutableSegmentMaxItemCount} records"),
      string.Create(CultureInfo.InvariantCulture, $"Sparse array step: {tuning.ZoneTreeSparseArrayStepSize}; key cache: {tuning.ZoneTreeKeyCacheSize}; value cache: {tuning.ZoneTreeValueCacheSize}"),
      "Background maintainer with bottom merges started on segment activation"
    ];
  }

  void StartBottomMerge(
      IZoneTreeMaintenance<VersionedKey<long>, VersionedValue<byte[]>> maintenance,
      IDiskSegment<VersionedKey<long>, VersionedValue<byte[]>> segment,
      bool isBottomSegment)
  {
    if (maintenance.IsBottomSegmentsMerging || maintenance.BottomSegments.Count < 2)
      return;
    Maintainer.StartBottomSegmentsMerge();
  }

  public IEngineSession OpenSession() => new Session(this);

  public void Settle()
  {
    Maintainer.EvictToDisk();
    Maintainer.WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
    Cluster.Maintenance.SaveMetaData();
  }

  public void Dispose()
  {
    try
    {
      Maintainer.WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
      Storage.OnDiskSegmentActivated -= StartBottomMerge;
      Maintainer.Dispose();
    }
    finally { Cluster.Dispose(); }
  }

  sealed class Session(ZoneTreeClusterStandaloneEngine engine) : IEngineSession
  {
    public IEngineReadTransaction BeginRead() =>
        new ReadTransaction(engine.Column.BeginReadOnlyTransaction());

    public IEngineTransaction BeginWrite() =>
        new WriteTransaction(engine.Column.BeginTransaction(engine.Isolation), engine.Durable);

    public void Dispose() { }
  }

  sealed class ReadTransaction(IStandaloneReadTransaction<long, byte[]> transaction) : IEngineReadTransaction
  {
    public bool TryGet(long key, out byte[] value) => transaction.TryGet(key, out value);
    public IEnumerable<KeyValuePair<long, byte[]>> Range(long from, long to) => transaction.Range(from, to);
    public void Dispose() => transaction.Dispose();
  }

  sealed class WriteTransaction(IStandaloneTransaction<long, byte[]> transaction, bool durable) : IEngineTransaction
  {
    public bool HasConflict => transaction.HasConflict;
    public bool TryGet(long key, out byte[] value) => transaction.TryGet(key, out value);
    public bool TryGetForUpdate(long key, out byte[] value) => transaction.TryGetForUpdate(key, out value);
    public IEnumerable<KeyValuePair<long, byte[]>> Range(long from, long to) => transaction.Range(from, to);
    public void Put(long key, byte[] value) => transaction.Upsert(key, value);
    public void Delete(long key) => transaction.Delete(key);
    public bool TryCommit() => durable
        ? transaction.TryCommitDurableAsync().GetAwaiter().GetResult()
        : transaction.TryCommit();
    public void Rollback() => transaction.Rollback();
    public void Dispose() => transaction.Dispose();
  }
}
