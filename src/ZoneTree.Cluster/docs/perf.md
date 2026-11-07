# ZoneTree.Cluster performance

Measurements made with `benchmarks/domain-transactions`. Each run takes at most 3 minutes.

## Transactions: the cluster against plain MVCC trees

Four ways to store 8 logical columns, all with the same total memory:

- **One MVCC tree:** the engine's MVCC tree, with the column encoded into the key.
- **Independent trees:** one engine MVCC tree per column. A transaction commits in each tree
  separately, so it is not atomic across columns. It shows what atomicity costs.
- **Cluster, one column:** a cluster domain with one column, the column encoded into the key.
- **Cluster, 8 columns:** a cluster domain with one coordinated column per logical column,
  committed atomically.

Transactions per second, all from one run on the machine below. The data is 100K records
with 100-byte values, and the memory budget is 2M mutable records in total, split across an
engine's trees. A span-4 transaction touches 4 columns. Each measurement runs 0.5 seconds
of warmup, then counts transactions for 2 seconds.

| Workload | Threads | One MVCC tree | Independent trees | Cluster, one column | Cluster, 8 columns |
| --- | --- | --- | --- | --- | --- |
| write, span 1 | 1 | 671K | 738K | 682K | 670K |
| write, span 1 | 16 | 669K | 1,593K | 790K | 1,159K |
| write, span 4 | 1 | 299K | 187K | 266K | 189K |
| write, span 4 | 16 | 346K | 485K | 392K | 352K |
| read, span 1 | 1 | 2,460K | 2,443K | 2,097K | 2,053K |
| read, span 1 | 16 | 9,606K | 25,048K | 12,895K | 12,068K |
| read, span 4 | 1 | 739K | 646K | 690K | 552K |
| read, span 4 | 16 | 6,190K | 6,362K | 6,980K | 6,269K |
| rmw, span 1 | 1 | 525K | 567K | 565K | 524K |
| rmw, span 1 | 16 | 591K | 1,729K | 727K | 1,207K |
| rmw, span 4 | 1 | 192K | 144K | 210K | 145K |
| rmw, span 4 | 16 | 307K | 451K | 348K | 322K |

**How far to trust a gap:** the engines run one after another within a run, and gaps of up
to about 20% can be noise. For example, this run shows 16-thread reads at 9.6M for one MVCC
tree and 12.9M for the cluster with one column. A direct comparison of just those two,
three times with alternating order, gave 11.6M to 12.3M for both: reads are the same. Repeated
runs of one cell on one build vary by up to about 7%.

What holds up beyond the noise:

- **Writes on one cluster column are faster than on one MVCC tree at 16 threads,** by about
  20% (790K against 669K, and 727K against 591K for rmw). A cluster transaction stores one
  status record per column, its commit record, after releasing its commit gate. One MVCC
  tree stores a pending record at the first write and the commit record under its commit
  lock.
- **At 1 thread, the two are close,** except span-4 writes, where one MVCC tree is faster
  (299K against 266K). The cluster does more work per transaction: it registers with the
  domain and its commit order, and creates a view per column.
- **Separate columns scale writes:** at 16 threads, 1,159K against 669K for one tree. Each
  column is its own tree, so writes to different columns run in parallel.
- **Atomicity across columns has a price.** Independent trees, which give no atomicity,
  write single columns faster (1,593K against 1,159K): every cluster commit takes a place in
  the domain's commit order and becomes visible in that order.
- **Transactions that touch many columns pay per column.** Each column a transaction writes
  adds a view and a commit record. At 1 thread, span-4 writes on 8 cluster columns run at
  189K against 299K on one tree, as fast as independent trees (187K), which give no
  atomicity.
- **Independent trees read fastest at 16 threads** because each tree registers its readers
  separately, so the threads do not share one counter.

Measured separately:

- **Async column operations did not help** on in-memory data: each one costs a thread pool
  hop, which is more than the operation itself.

**To rerun this table** (about 2 minutes), from `benchmarks/domain-transactions/src`:

```
dotnet run -c Release -- --keys 100K --mutable-segment 2M --workload write,read,rmw --span 1,4 --threads 1,16 --seconds 2 --warmup 0.5
```

## Durable commits

A durable commit returns once it survives a power loss. On one column, the cluster commits
durably as fast as one MVCC tree, and faster at 16 threads. Across 8 columns it is slower,
because each column is its own WAL file on the disk.

**Setup:** the data and storage of the table above, with every commit durable
(`TryCommitDurableAsync` in both), blind writes only. Each measurement runs 0.5 seconds of warmup, then counts transactions for 2 seconds.
One run.

**Results**, transactions per second:

| Workload | Threads | One MVCC tree | Cluster, one column | Cluster, 8 columns |
| --- | --- | --- | --- | --- |
| write, span 1 | 1 | 815 | 801 | 785 |
| write, span 1 | 16 | 5,144 | 5,986 | 2,075 |
| write, span 4 | 1 | 812 | 791 | 344 |
| write, span 4 | 16 | 4,488 | 5,431 | 1,693 |

**Why:**
- **One column:** a durable commit waits for one sync round, which syncs the column's WAL
  once, as the tree syncs its own. Concurrent durable commits share a round, so at 16
  threads one round covers many commits.
- **8 columns:** a round syncs every column the domain wrote since the last round, in
  parallel, but the files share one disk, which handles the syncs about one after another. A
  span-4 commit at 1 thread waits for about 4 syncs: 344 against 785 for span 1. At 16
  threads a round covers up to 8 column files.

**To rerun** (about 40 seconds), from `benchmarks/domain-transactions/src`:

```
dotnet run -c Release -- --keys 100K --mutable-segment 2M --engine mvcc-one-tree,cluster-one-column,cluster-columns --durable --workload write --span 1,4 --threads 1,16 --seconds 2 --warmup 0.5
```

## Standalone columns against a plain MVCC tree

A cluster standalone column wraps one engine MVCC tree. It performs about the same as using
that tree directly. The writes at 16 threads may be a few percent slower.

**Setup:** the machine, data and storage of the raw columns below. Each measurement runs
0.5 seconds of warmup, then counts transactions for 2 seconds. Both engines use
serializable isolation, and every logical column is encoded into the key of the one tree.
The table averages four runs, alternating which engine runs first.

**Results**, transactions per second, averaged over the four runs:

| Workload | Threads | One MVCC tree | Standalone column | Ratio |
| --- | --- | --- | --- | --- |
| write | 1 | 653K | 651K | 1.00× |
| write | 16 | 548K | 511K | 0.93× |
| read | 1 | 1,153K | 1,106K | 0.96× |
| read | 16 | 9,705K | 9,613K | 0.99× |
| rmw | 1 | 273K | 261K | 0.96× |
| rmw | 16 | 482K | 477K | 0.99× |

**Why they match:** a standalone transaction shares no state with other threads beyond
what the MVCC tree does.
- **Drops:** the operations and commits of a transaction check that the column was not
  dropped. A transaction registers with its column only when it opens an iterator or a
  range, because the MVCC tree's iterators know nothing of the column; a drop then ends the
  transaction and its iterators.
- **Read-only transactions** register nothing else.
- **Writing transactions** register with the cluster, which rolls them back when it closes.
  This is likely the few percent on writes at 16 threads.

**To rerun** (about 35 seconds per run), from `benchmarks/domain-transactions/src`:

```
dotnet run -c Release -- --keys 1M --engine mvcc-one-tree,cluster-standalone --workload write,read,rmw --span 1 --threads 1,16 --seconds 2 --warmup 0.5
```

Swap the order of the engines to check for order effects.

## Raw columns against a plain ZoneTree

A cluster raw column performs the same as a plain ZoneTree, within measurement noise.

**Setup:**
- **Machine:** Intel Core Ultra 7 265KF (20 cores), 64 GB RAM, Samsung 960 EVO SSD,
  Windows 11, .NET 10, server GC, `DOTNET_TieredCompilation=0`.
- **Data:** 1M records, `long` keys, 100-byte values, single-key operations (span 1).
- **Storage, the same for both:**
  - AsyncShared WAL without compression;
  - a 250K-record mutable segment;
  - sparse array step 16;
  - 1,024 cached keys and values per segment;
  - a background maintainer.
- **Each measurement** (one engine, one workload, one thread count) runs 0.5 seconds of
  warmup, then counts operations for 3 seconds. It starts with the records on disk and no
  merge running.
- **Runs:** four, alternating which engine runs first.

**Engines:**
- **`zonetree-raw`:** a plain `ZoneTree<long, byte[]>` with `Int64ComparerAscending` and
  `Int64Serializer`.
- **`cluster-raw`:** a cluster raw column. The cluster's built-in `int64` encoding uses the
  same comparer and serializer, and the value serializer is the same.

**Results**, operations per second, averaged over the four runs:

| Workload | Threads | `zonetree-raw` | `cluster-raw` | Ratio | `zonetree-raw` range |
| --- | --- | --- | --- | --- | --- |
| write | 4 | 3.30M | 3.31M | 1.00× | 2.96M–3.46M |
| write | 16 | 3.00M | 3.12M | 1.04× | 2.70M–3.14M |
| read | 4 | 7.20M | 7.04M | 0.98× | 7.04M–7.29M |
| read | 16 | 23.7M | 22.4M | 0.94× | 21.4M–25.6M |

- **1 thread, and the rmw and mixed workloads:** identical within ±5%, from two 2-second
  runs in each engine order.
- **Reads at 16 threads:** the 6% average gap lies inside the range of the plain ZoneTree's
  own runs, so it cannot be told apart from noise.

**Why they match:** a raw column operation checks two flags, dropped and disposed, then
calls the same tree. The flags are plain reads, and nothing is written per operation, so
threads do not share any written cache line. The only other cost is one extra interface
call.

**To rerun** (about 70 seconds), from `benchmarks/domain-transactions/src`:

```
dotnet run -c Release -- --keys 1M --engine zonetree-raw,cluster-raw --workload write,read,rmw,mixed --span 1 --threads 1,4,16 --seconds 2 --warmup 0.5
```

Swap the order of the engines to check for order effects.
