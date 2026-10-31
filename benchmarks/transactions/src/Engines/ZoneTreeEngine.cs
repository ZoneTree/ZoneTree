using System.Globalization;
using ZoneTree;
using ZoneTree.Comparers;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Segments;
using ZoneTree.Serializers;
using ZoneTree.Transactional;
using ZoneTree.Transactional.Versioning;

namespace Transactions.Benchmark.Engines;

public sealed class ZoneTreeEngine : ITransactionEngine
{
  readonly ITransactionalZoneTree<long, byte[]> Tree;
  readonly IMaintainer Maintainer;
  readonly IZoneTreeMaintenance<VersionedKey<long>, VersionedValue<byte[]>> Storage;
  readonly TransactionIsolationLevel Isolation;
  readonly bool Durable;

  public IReadOnlyList<string> Settings { get; }

  public ZoneTreeEngine(EngineOpenOptions options, TransactionIsolationLevel isolation, WriteAheadLogMode walMode)
  {
    var tuning = options.Tuning;
    Isolation = isolation;
    Durable = options.Durability == DurabilityMode.PerCommit;
    Tree = new ZoneTreeFactory<long, byte[]>()
        .SetDataDirectory(options.Directory)
        .SetMutableSegmentMaxItemCount(tuning.ZoneTreeMutableSegmentMaxItemCount)
        .SetLogLevel(LogLevel.Error)
        .SetComparer(new Int64ComparerAscending())
        .SetKeySerializer(new Int64Serializer())
        .SetValueSerializer(new ValueSerializer())
        .ConfigureDiskSegmentOptions(disk =>
        {
          disk.DefaultSparseArrayStepSize = tuning.ZoneTreeSparseArrayStepSize;
          disk.KeyCacheSize = tuning.ZoneTreeKeyCacheSize;
          disk.ValueCacheSize = tuning.ZoneTreeValueCacheSize;
        })
        .ConfigureWriteAheadLogOptions(wal => wal.WriteAheadLogMode = walMode)
        .OpenOrCreateTransactional();

    Maintainer = Tree.CreateMaintainer();
    Maintainer.BlockCacheLifeTime = TimeSpan.FromMinutes(1);
    Maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromSeconds(30);
    Storage = Tree.Maintenance.ZoneTree.Maintenance;
    Storage.OnDiskSegmentActivated += StartBottomMerge;

    Settings =
    [
      $"Isolation: {isolation}",
      Durable
          ? $"WAL: {walMode}; every commit waits for a shared WAL sync (TryCommitDurableAsync)"
          : walMode == WriteAheadLogMode.SyncCompressed
              ? "WAL: SyncCompressed; commits go to a memory-mapped tail, a full block is synced, and the tail is synced periodically"
              : $"WAL: {walMode}; the background writer syncs on its own, and commits do not wait",
      string.Create(CultureInfo.InvariantCulture, $"Mutable segment: {tuning.ZoneTreeMutableSegmentMaxItemCount} records"),
      string.Create(CultureInfo.InvariantCulture, $"Sparse array step: {tuning.ZoneTreeSparseArrayStepSize}; key cache: {tuning.ZoneTreeKeyCacheSize}; value cache: {tuning.ZoneTreeValueCacheSize}"),
      "Background maintainer with bottom merges started on segment activation",
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
    Tree.Maintenance.SaveMetaData();
  }

  public void Dispose()
  {
    Maintainer.WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
    Storage.OnDiskSegmentActivated -= StartBottomMerge;
    Maintainer.Dispose();
    Tree.Dispose();
  }

  sealed class Session(ZoneTreeEngine engine) : IEngineSession
  {
    public IEngineReadTransaction BeginRead() =>
        new ReadTransaction(engine.Tree.BeginReadOnlyTransaction());

    public IEngineTransaction BeginWrite() =>
        new WriteTransaction(engine.Tree.BeginTransaction(engine.Isolation), engine.Durable);

    public void Dispose()
    {
    }
  }

  sealed class ReadTransaction(IReadTransaction<long, byte[]> transaction) : IEngineReadTransaction
  {
    public bool TryGet(long key, out byte[] value) => transaction.TryGet(key, out value);

    public IEnumerable<KeyValuePair<long, byte[]>> Range(long from, long to) =>
        transaction.Range(from, to);

    public void Dispose() => transaction.Dispose();
  }

  sealed class WriteTransaction(ITransaction<long, byte[]> transaction, bool durable) : IEngineTransaction
  {
    public bool HasConflict => transaction.HasConflict;

    public bool TryGet(long key, out byte[] value) => transaction.TryGet(key, out value);

    public IEnumerable<KeyValuePair<long, byte[]>> Range(long from, long to) =>
        transaction.Range(from, to);

    public bool TryGetForUpdate(long key, out byte[] value) =>
        transaction.TryGetForUpdate(key, out value);

    public void Put(long key, byte[] value) => transaction.Upsert(key, value);

    public void Delete(long key) => transaction.Delete(key);

    public bool TryCommit() =>
        durable
            ? transaction.TryCommitDurableAsync().GetAwaiter().GetResult()
            : transaction.TryCommit();

    public void Rollback() => transaction.Rollback();

    public void Dispose() => transaction.Dispose();
  }

  sealed class ValueSerializer : ISerializer<byte[]>
  {
    public byte[] Deserialize(Memory<byte> bytes) => bytes.ToArray();

    public Memory<byte> Serialize(in byte[] entry) => entry;
  }
}
