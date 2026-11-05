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
    var factory = CreateFactory(options, walMode);
    Tree = factory.OpenOrCreateTransactional();
    var effectiveWalMode = factory.Options.WriteAheadLogOptions.WriteAheadLogMode;

    Maintainer = Tree.CreateMaintainer();
    Maintainer.BlockCacheLifeTime = TimeSpan.FromMinutes(1);
    Maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromSeconds(30);
    Storage = Tree.Maintenance.ZoneTree.Maintenance;
    Storage.OnDiskSegmentActivated += StartBottomMerge;

    Settings =
    [
      $"Isolation: {isolation}",
      Durable
          ? $"WAL: {effectiveWalMode}; compression disabled; TryCommitDurableAsync syncs the shared WAL before publishing each commit"
          : effectiveWalMode == WriteAheadLogMode.Shared
              ? "WAL: Shared; compression disabled; appends use a memory-mapped tail; periodic flushes sync the complete dirty journal; recovery retains a commit prefix"
              : $"WAL: {effectiveWalMode}; compression disabled; the background writer syncs the complete dirty journal when its queue empties; ordinary commits can be visible before durability; recovery retains a commit prefix",
      string.Create(CultureInfo.InvariantCulture, $"Mutable segment: {tuning.ZoneTreeMutableSegmentMaxItemCount} records"),
      string.Create(CultureInfo.InvariantCulture, $"Sparse array step: {tuning.ZoneTreeSparseArrayStepSize}; key cache: {tuning.ZoneTreeKeyCacheSize}; value cache: {tuning.ZoneTreeValueCacheSize}"),
      "Background maintainer with bottom merges started on segment activation",
    ];
  }

  internal static ZoneTreeFactory<long, byte[]> CreateFactory(EngineOpenOptions options, WriteAheadLogMode walMode)
      => ConfigureFactory(new ZoneTreeFactory<long, byte[]>().SetDataDirectory(options.Directory), options, walMode);

  internal static ZoneTreeFactory<long, byte[]> ConfigureFactory(
      ZoneTreeFactory<long, byte[]> factory, EngineOpenOptions options, WriteAheadLogMode walMode)
      => ConfigureStorage(factory
          .SetComparer(new Int64ComparerAscending())
          .SetKeySerializer(new Int64Serializer())
          .SetValueSerializer(new ValueSerializer()), options, walMode);

  /// <summary>The storage tuning without the comparer and serializers.</summary>
  internal static ZoneTreeFactory<long, byte[]> ConfigureStorage(
      ZoneTreeFactory<long, byte[]> factory, EngineOpenOptions options, WriteAheadLogMode walMode)
  {
    var tuning = options.Tuning;
    return factory
        .SetMutableSegmentMaxItemCount(tuning.ZoneTreeMutableSegmentMaxItemCount)
        .ConfigureDiskSegmentOptions(disk =>
        {
          disk.DefaultSparseArrayStepSize = tuning.ZoneTreeSparseArrayStepSize;
          disk.KeyCacheSize = tuning.ZoneTreeKeyCacheSize;
          disk.ValueCacheSize = tuning.ZoneTreeValueCacheSize;
        })
        .ConfigureWriteAheadLogOptions(wal =>
        {
          wal.CompressionMethod = CompressionMethod.None;
          wal.WriteAheadLogMode = walMode;
          wal.CompressionBlockSize = 1024 * 256;
        });
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

    // Durable commits sync their record before publication; ordinary commits
    // can become visible before syncing. Concurrent durable commits share fsyncs.
    public bool TryCommit() =>
        durable
            ? transaction.TryCommitDurableAsync().GetAwaiter().GetResult()
            : transaction.TryCommit();

    public void Rollback() => transaction.Rollback();

    public void Dispose() => transaction.Dispose();
  }

  internal sealed class ValueSerializer : ISerializer<byte[]>
  {
    public byte[] Deserialize(Memory<byte> bytes) => bytes.ToArray();

    public Memory<byte> Serialize(in byte[] entry) => entry;
  }
}
