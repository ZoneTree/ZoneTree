using ZoneTree.Options;
using ZoneTree.Transactional;

namespace Transactions.Benchmark.Engines;

public enum DurabilityMode
{
  /// <summary>A commit is visible at once and reaches the disk later.</summary>
  Relaxed,

  /// <summary>A commit returns after its data is synced to the disk.</summary>
  PerCommit
}

public sealed record EngineOpenOptions(
    string Directory,
    DurabilityMode Durability,
    BenchmarkOptions Tuning);

/// <summary>
/// An opened database. Scenarios use one session per worker thread.
/// </summary>
public interface ITransactionEngine : IDisposable
{
  /// <summary>
  /// The settings that decide the results, for the report.
  /// </summary>
  IReadOnlyList<string> Settings { get; }

  IEngineSession OpenSession();

  /// <summary>
  /// Moves the buffered data to its files and waits for the started maintenance.
  /// Every session must be closed.
  /// </summary>
  void Settle();
}

/// <summary>
/// The per-thread state of an engine. A session runs one transaction at a time.
/// </summary>
public interface IEngineSession : IDisposable
{
  IEngineReadTransaction BeginRead();

  IEngineTransaction BeginWrite();
}

public interface IEngineReadTransaction : IDisposable
{
  bool TryGet(long key, out byte[] value);

  /// <summary>
  /// Reads the records between two keys, inclusive, in ascending key order.
  /// A session runs one range at a time.
  /// </summary>
  IEnumerable<KeyValuePair<long, byte[]>> Range(long from, long to);
}

/// <summary>
/// A read-write transaction. Reads and writes report conflicts through
/// <see cref="HasConflict"/>, so a scenario retries from its commit on every engine.
/// </summary>
public interface IEngineTransaction : IEngineReadTransaction
{
  bool HasConflict { get; }

  bool TryGetForUpdate(long key, out byte[] value);

  void Put(long key, byte[] value);

  void Delete(long key);

  /// <returns>false if the transaction conflicted and was rolled back.</returns>
  bool TryCommit();

  void Rollback();
}

public sealed record EngineVariant(
    string Id,
    string Family,
    string DisplayName,
    string Isolation,
    Func<EngineOpenOptions, ITransactionEngine> Open);

public static class EngineCatalog
{
  const string SerializableIsolation =
      "Serializable: a commit fails if a newer commit wrote a key it wrote or read, or a key inside a range it read.";

  const string SnapshotIsolation =
      "Snapshot: a commit fails if a newer commit wrote a key it wrote or read for update. Plain reads are not protected.";

  /// <summary>
  /// The order is the report's column order and fixes each engine's chart color,
  /// so engines of a family stay together and share a color family in SvgCharts.
  /// </summary>
  public static readonly IReadOnlyList<EngineVariant> All =
  [
    new(
        "zonetree-mvcc-ser-async",
        "zonetree",
        "ZoneTree MVCC serializable async",
        SerializableIsolation,
        options => new ZoneTreeEngine(options, TransactionIsolationLevel.Serializable, WriteAheadLogMode.AsyncCompressed)),
    new(
        "zonetree-mvcc-snap-async",
        "zonetree",
        "ZoneTree MVCC snapshot async",
        SnapshotIsolation,
        options => new ZoneTreeEngine(options, TransactionIsolationLevel.Snapshot, WriteAheadLogMode.AsyncCompressed)),
    new(
        "zonetree-mvcc-ser-sync",
        "zonetree",
        "ZoneTree MVCC serializable sync",
        SerializableIsolation,
        options => new ZoneTreeEngine(options, TransactionIsolationLevel.Serializable, WriteAheadLogMode.SyncCompressed)),
    new(
        "zonetree-mvcc-snap-sync",
        "zonetree",
        "ZoneTree MVCC snapshot sync",
        SnapshotIsolation,
        options => new ZoneTreeEngine(options, TransactionIsolationLevel.Snapshot, WriteAheadLogMode.SyncCompressed)),
    new(
        "rocksdb-optimistic",
        "rocksdb",
        "RocksDB optimistic",
        "OptimisticTransactionDB with a snapshot per transaction: a commit fails if a newer commit wrote a key it wrote or read for update. Plain reads and ranges are not protected.",
        options => new RocksDbEngine(options, optimistic: true)),
    new(
        "rocksdb-pessimistic",
        "rocksdb",
        "RocksDB pessimistic",
        "TransactionDB with key locks and deadlock detection, no snapshot: writes and reads for update lock the key and read the latest value. Plain reads see the latest commit, so they are not repeatable.",
        options => new RocksDbEngine(options, optimistic: false)),
    new(
        "sqlite",
        "sqlite",
        "SQLite",
        "WAL mode with BEGIN IMMEDIATE: one write transaction at a time, so writes are serializable and never conflict. Readers keep their snapshot and do not block.",
        options => new SqliteEngine(options)),
  ];

  public static int ColorSlot(string engineId)
  {
    for (var i = 0; i < All.Count; i++)
    {
      if (All[i].Id == engineId)
        return i + 1;
    }
    throw new ArgumentException($"Unknown engine '{engineId}'.", nameof(engineId));
  }

  public static IReadOnlyList<EngineVariant> Resolve(IEnumerable<string> selectors)
  {
    var selected = new HashSet<string>(StringComparer.Ordinal);
    foreach (var selector in selectors)
    {
      var matches = All
          .Where(x => selector == "all" || x.Id == selector || x.Family == selector)
          .ToArray();
      if (matches.Length == 0)
      {
        throw new ArgumentException(
            $"Unknown engine '{selector}'. Use all, {string.Join(", ", All.Select(x => x.Family).Distinct())} or {string.Join(", ", All.Select(x => x.Id))}.");
      }
      foreach (var match in matches)
        selected.Add(match.Id);
    }
    return All.Where(x => selected.Contains(x.Id)).ToArray();
  }
}
