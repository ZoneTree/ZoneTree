using Transactions.Benchmark.Engines;

namespace Transactions.Benchmark.Scenarios;

public static class ScenarioCatalog
{
  /// <summary>
  /// The categories in report order, each with the question it answers.
  /// </summary>
  public static readonly IReadOnlyList<(string Id, string Title, string Question)> Categories =
  [
    ("single-key", "Single-key updates", "How fast are the smallest transactions when contention is low?"),
    ("contention", "Contention", "What happens as more workers fight for fewer keys?"),
    ("multi-key", "Multi-key transactions", "What do realistic multi-key units of work and batches cost?"),
    ("mixed", "Reads and writes in transactions", "What do read-heavy workloads cost, and what does validating reads cost?"),
    ("snapshot-reads", "Snapshot reads", "How fast are consistent reads, and how much do writers disturb them?"),
    ("isolation", "Isolation", "Which anomalies does each engine allow, and what does preventing them cost?"),
    ("failure", "Rollbacks", "What do abandoned transactions cost?"),
    ("large", "Large transactions", "What does one huge transaction cost in time and memory?"),
    ("durability", "Durable commits", "What does waiting for the disk cost, and do concurrent commits share syncs?"),
  ];

  /// <summary>
  /// The job sizes are for 100K keys and --scale 1, and grow with both. They differ per
  /// scenario because the rates do: at that size, each job should take seconds on the
  /// slowest engine, not minutes.
  /// </summary>
  public static readonly IReadOnlyList<Scenario> All =
  [
    new RmwScenario(
        "rmw-uniform",
        "single-key",
        "Read-modify-write, uniform keys",
        "Counters and small state updates spread over many keys.",
        context => context.Keys,
        (_, keys) => KeyPickers.Uniform(keys),
        job: 500_000),
    new RmwScenario(
        "rmw-zipf",
        "single-key",
        "Read-modify-write, skewed keys",
        "Real traffic, where a few keys take most of the updates.",
        context => context.Keys,
        (context, keys) => KeyPickers.Zipf(keys, context.Options.ZipfTheta),
        job: 500_000),
    new InsertScenario(
        "insert-unique",
        "single-key",
        "Insert new keys",
        "Event logs, new records with hashed ids: inserts that cannot conflict.",
        batchSize: 1,
        scattered: true,
        job: 300_000),

    new RmwScenario(
        "hot-1",
        "contention",
        "All workers update one key",
        "A global counter or sequence: the worst case for optimistic concurrency.",
        _ => 1,
        (_, _) => _ => 0,
        job: 100_000),
    new RmwScenario(
        "hot-16",
        "contention",
        "All workers update 16 keys",
        "Per-tenant or per-shard counters with few tenants.",
        _ => 16,
        (_, keys) => KeyPickers.Uniform(keys),
        job: 300_000),
    new RmwScenario(
        "hot-256",
        "contention",
        "All workers update 256 keys",
        "A small hot set under moderate contention.",
        _ => 256,
        (_, keys) => KeyPickers.Uniform(keys),
        job: 300_000),

    new TransferScenario(),
    new OrderEntryScenario(),
    new InsertScenario(
        "batch-10",
        "multi-key",
        "Batches of 10 inserts",
        "Writing a record with its index entries.",
        batchSize: 10,
        scattered: false,
        job: 100_000),
    new InsertScenario(
        "batch-100",
        "multi-key",
        "Batches of 100 inserts",
        "Grouped ingestion from a queue.",
        batchSize: 100,
        scattered: false,
        job: 20_000),
    new InsertScenario(
        "batch-1000",
        "multi-key",
        "Batches of 1000 inserts",
        "Bulk loads split into transactions.",
        batchSize: 1_000,
        scattered: false,
        job: 2_000),

    new ReadWriteMixScenario(
        "mixed-95-5",
        "95% reads, 5% updates",
        "Read-mostly services with skewed keys.",
        readOnlyPercent: 95,
        readsPerWriteTransaction: 0,
        writesPerWriteTransaction: 1,
        job: 2_000_000),
    new ReadWriteMixScenario(
        "read-8-write-2",
        "8 reads and 2 updates per transaction",
        "Transactions that decide from several keys. Serializable engines validate the reads, others do not.",
        readOnlyPercent: 0,
        readsPerWriteTransaction: 8,
        writesPerWriteTransaction: 2,
        job: 200_000),

    new SnapshotPointScenario(
        "snapshot-point",
        "10 point reads per snapshot, no writers",
        "The read path alone: caches and lookups serving consistent reads.",
        withWriters: false,
        job: 1_000_000),
    new SnapshotPointScenario(
        "snapshot-point-under-write",
        "10 point reads per snapshot, 2 writers",
        "Consistent reads while the data changes.",
        withWriters: true,
        job: 500_000),
    new SnapshotRangeScenario(
        "range-100-under-write",
        "Scans of 100 keys, 2 writers",
        "Listing rows of one customer or page while they change.",
        fullScan: false),
    new SnapshotRangeScenario(
        "full-scan-under-write",
        "Full scans, 2 writers",
        "Analytics and exports over live data.",
        fullScan: true),
    new LongReaderScenario(),

    new WriteSkewScenario(guarded: false),
    new WriteSkewScenario(guarded: true),
    new PhantomScenario(guarded: false),
    new PhantomScenario(guarded: true),

    new RollbackScenario(
        "rollback-all",
        "Every transaction rolls back",
        "The pure cost of abandoning 5 writes.",
        rollbackPercent: 100,
        job: 300_000),
    new RollbackScenario(
        "rollback-10pct",
        "10% of transactions roll back",
        "Business rules that reject some of the work.",
        rollbackPercent: 10,
        job: 200_000),

    new LargeTransactionScenario(),

    new RmwScenario(
        "durable-rmw",
        "durability",
        "Durable read-modify-write",
        "Every commit must survive a power loss before it is acknowledged.",
        context => context.Keys,
        (_, keys) => KeyPickers.Uniform(keys),
        job: 20_000,
        DurabilityMode.PerCommit),
    new InsertScenario(
        "durable-batch-100",
        "durability",
        "Durable batches of 100 inserts",
        "Grouped ingestion that acknowledges only durable batches.",
        batchSize: 100,
        scattered: false,
        job: 10_000,
        DurabilityMode.PerCommit),
    new TransferScenario(durable: true),
    new OrderEntryScenario(durable: true),
  ];

  public static Scenario Find(string id) =>
      All.FirstOrDefault(x => x.Id == id)
          ?? throw new ArgumentException($"Unknown scenario '{id}'. Use --list-scenarios.");

  public static IReadOnlyList<Scenario> Resolve(IEnumerable<string> selectors)
  {
    var selected = new HashSet<string>(StringComparer.Ordinal);
    foreach (var selector in selectors)
    {
      if (selector == "all")
      {
        selected.UnionWith(All.Select(x => x.Id));
      }
      else if (selector.StartsWith("category:", StringComparison.Ordinal))
      {
        var category = selector["category:".Length..];
        if (Categories.All(x => x.Id != category))
          throw new ArgumentException($"Unknown category '{category}'. Use {string.Join(", ", Categories.Select(x => x.Id))}.");
        selected.UnionWith(All.Where(x => x.Category == category).Select(x => x.Id));
      }
      else
      {
        selected.Add(Find(selector).Id);
      }
    }
    return All.Where(x => selected.Contains(x.Id)).ToArray();
  }
}
