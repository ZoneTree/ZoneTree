using System.Globalization;
using ZoneTree;
using ZoneTree.Options;
using ZoneTree.Segments;
using ZoneTree.Transactional;
using ZoneTree.Cluster;
using ZoneTree.Transactional.Versioning;

namespace Transactions.Benchmark.Engines;

/// <summary>Runs the existing single-keyspace scenarios through the ZoneTreeCluster API.</summary>
public sealed class ZoneTreeClusterEngine : ITransactionEngine
{
  readonly IZoneTreeClusterHost Cluster;
  readonly IClusterDomain Domain;
  readonly ICoordinatedColumn<long, byte[]> Column;
  readonly IMaintainer Maintainer;
  readonly IZoneTreeMaintenance<VersionedKey<long>, VersionedValue<byte[]>> Storage;
  readonly bool Durable;

  public IReadOnlyList<string> Settings { get; }

  public ZoneTreeClusterEngine(EngineOpenOptions options, WriteAheadLogMode walMode)
  {
    var tuning = options.Tuning;
    Durable = options.Durability == DurabilityMode.PerCommit;
    Cluster = new ZoneTreeClusterFactory().OpenOrCreateInProcess(new ZoneTreeClusterOptions
    {
      DataDirectory = options.Directory,
      Domains =
      [
        new DomainDefinition("records")
        {
          Columns =
          [
            new CoordinatedColumnDefinition<long, byte[]>("records")
            {
              ConfigureStorage = factory => ZoneTreeEngine.ConfigureFactory(factory, options, walMode)
            }
          ]
        }
      ]
    });
    Domain = Cluster.GetDomain("records");
    Column = Domain.GetColumn<long, byte[]>("records");
    Maintainer = Cluster.CreateColumnMaintainer("records");
    Maintainer.BlockCacheLifeTime = TimeSpan.FromMinutes(1);
    Maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromSeconds(30);
    Storage = Cluster.GetCoordinatedColumnMaintenance<long, byte[]>("records").ZoneTree.Maintenance;
    Storage.OnDiskSegmentActivated += StartBottomMerge;
    Settings =
    [
      "Cluster API: one long/byte[] column; cross-column behavior is not measured",
      "Isolation: Serializable within the benchmark column",
      $"WAL: {walMode}; compression disabled; one shared journal for the column",
      Durable
          ? "TryCommitDurableAsync waits for the column commit to become durable before publication"
          : "Ordinary commits do not request a disk sync; AsyncShared can lose queued records on process crash",
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
    if (!maintenance.IsBottomSegmentsMerging && maintenance.BottomSegments.Count >= 2)
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

  sealed class Session(ZoneTreeClusterEngine engine) : IEngineSession
  {
    public IEngineReadTransaction BeginRead()
    {
      var transaction = engine.Domain.BeginReadOnlyTransaction();
      try { return new ReadTransaction(transaction, transaction.GetColumn(engine.Column)); }
      catch { transaction.Dispose(); throw; }
    }

    public IEngineTransaction BeginWrite()
    {
      var transaction = engine.Domain.BeginTransaction(TransactionIsolationLevel.Serializable);
      try { return new WriteTransaction(transaction, transaction.GetColumn(engine.Column), engine.Durable); }
      catch { transaction.Dispose(); throw; }
    }

    public void Dispose() { }
  }

  sealed class ReadTransaction(IDomainReadTransaction transaction, IReadColumn<long, byte[]> column)
      : IEngineReadTransaction
  {
    public bool TryGet(long key, out byte[] value) => column.TryGet(key, out value);
    public IEnumerable<KeyValuePair<long, byte[]>> Range(long from, long to) => column.Range(from, to);
    public void Dispose() => transaction.Dispose();
  }

  sealed class WriteTransaction(IDomainTransaction transaction,
      IWriteColumn<long, byte[]> column, bool durable) : IEngineTransaction
  {
    public bool HasConflict => transaction.HasConflict;
    public bool TryGet(long key, out byte[] value) => column.TryGet(key, out value);
    public bool TryGetForUpdate(long key, out byte[] value) => column.TryGetForUpdate(key, out value);
    public IEnumerable<KeyValuePair<long, byte[]>> Range(long from, long to) => column.Range(from, to);
    public void Put(long key, byte[] value) => column.Upsert(key, value);
    public void Delete(long key) => column.Delete(key);
    public bool TryCommit() => durable
        ? transaction.TryCommitDurableAsync().GetAwaiter().GetResult()
        : transaction.TryCommit();
    public void Rollback() => transaction.Rollback();
    public void Dispose() => transaction.Dispose();
  }
}
