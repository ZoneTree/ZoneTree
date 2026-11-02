using System.Globalization;
using ZoneTree;
using ZoneTree.Options;
using ZoneTree.Segments;
using ZoneTree.Serializers;

namespace Transactions.Benchmark.Engines;

/// <summary>Runs the scenario API directly against a plain ZoneTree.</summary>
public sealed class ZoneTreeRawEngine : ITransactionEngine
{
  readonly IZoneTree<long, byte[]> Tree;
  readonly IMaintainer Maintainer;
  readonly bool Durable;

  public IReadOnlyList<string> Settings { get; }

  public ZoneTreeRawEngine(EngineOpenOptions options, WriteAheadLogMode walMode)
  {
    var tuning = options.Tuning;
    Durable = options.Durability == DurabilityMode.PerCommit;
    Tree = ZoneTreeEngine.CreateFactory(options, walMode)
        .SetValueSerializer(new ValueSerializer())
        .OpenOrCreate();
    Maintainer = Tree.CreateMaintainer();
    Maintainer.BlockCacheLifeTime = TimeSpan.FromMinutes(1);
    Maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromSeconds(30);
    Tree.Maintenance.OnDiskSegmentActivated += StartBottomMerge;
    Settings =
    [
      "Raw: direct reads and writes; no snapshots, conflict validation, atomic batches or rollback",
      $"WAL: {walMode}; compression disabled",
      Durable ? "SyncWALAsync at each scenario commit boundary" : "Scenario commit is a no-op",
      string.Create(CultureInfo.InvariantCulture, $"Mutable segment: {tuning.ZoneTreeMutableSegmentMaxItemCount} records"),
      string.Create(CultureInfo.InvariantCulture, $"Sparse array step: {tuning.ZoneTreeSparseArrayStepSize}; key cache: {tuning.ZoneTreeKeyCacheSize}; value cache: {tuning.ZoneTreeValueCacheSize}"),
      "Background maintainer with bottom merges started on segment activation"
    ];
  }

  void StartBottomMerge(IZoneTreeMaintenance<long, byte[]> maintenance, IDiskSegment<long, byte[]> segment, bool isBottomSegment)
  {
    if (!maintenance.IsBottomSegmentsMerging && maintenance.BottomSegments.Count >= 2)
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
    Tree.Maintenance.OnDiskSegmentActivated -= StartBottomMerge;
    Maintainer.Dispose();
    Tree.Dispose();
  }

  // Reuse one operation scope per worker; raw mode creates no transaction objects.
  sealed class Session(ZoneTreeRawEngine engine) : IEngineSession, IEngineTransaction
  {
    bool HasWrites;

    public bool HasConflict => false;

    public IEngineReadTransaction BeginRead() => this;

    public IEngineTransaction BeginWrite()
    {
      HasWrites = false;
      return this;
    }

    public bool TryGet(long key, out byte[] value) => engine.Tree.TryGet(key, out value);

    public bool TryGetForUpdate(long key, out byte[] value) => TryGet(key, out value);

    public IEnumerable<KeyValuePair<long, byte[]>> Range(long from, long to)
    {
      using var iterator = engine.Tree.CreateIterator(IteratorType.AutoRefresh);
      iterator.Seek(from);
      while (iterator.Next())
      {
        if (iterator.CurrentKey > to)
          yield break;
        yield return iterator.Current;
      }
    }

    public void Put(long key, byte[] value)
    {
      engine.Tree.Upsert(key, value);
      HasWrites = true;
    }

    public void Delete(long key)
    {
      engine.Tree.ForceDelete(key);
      HasWrites = true;
    }

    public bool TryCommit()
    {
      if (engine.Durable && HasWrites)
        engine.Tree.SyncWALAsync().GetAwaiter().GetResult();
      return true;
    }

    public void Rollback() { }

    public void Dispose() { }
  }

  sealed class ValueSerializer : ISerializer<byte[]>
  {
    // Benchmark values are nonempty. Preserve null tombstones when reopening.
    public byte[] Deserialize(Memory<byte> bytes) => bytes.IsEmpty ? null! : bytes.ToArray();

    public Memory<byte> Serialize(in byte[] value) => value;
  }
}
