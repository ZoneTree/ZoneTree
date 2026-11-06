using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime;
using ZoneTree.Options;
using ZoneTree.Transactional;

namespace DomainTransactions.Benchmark;

/// <summary>The settings of a run.</summary>
sealed record BenchmarkOptions
{
  /// <summary>The records preloaded across all logical columns.</summary>
  public long Keys { get; init; } = 1_000_000;
  public int Columns { get; init; } = 8;

  /// <summary>The logical columns each transaction touches, one key in each.</summary>
  public int[] Spans { get; init; } = [1, 4];
  public int[] Threads { get; init; } = [1, 2, 4, 8, 16];
  public string[] Workloads { get; init; } = Workload.Names;
  public string[] Engines { get; init; } = EngineCatalog.Names;
  public double Seconds { get; init; } = 5;
  public double WarmupSeconds { get; init; } = 1;
  public int ValueSize { get; init; } = 100;
  public TransactionIsolationLevel Isolation { get; init; } = TransactionIsolationLevel.Serializable;
  public WriteAheadLogMode WalMode { get; init; } = WriteAheadLogMode.AsyncShared;
  public bool Durable { get; init; }

  /// <summary>True to run the cluster engines through their async API, blocking on each call.</summary>
  public bool AsyncApi { get; init; }

  /// <summary>True to give every tree the whole memory budget instead of a share of it.</summary>
  public bool PerTreeBudget { get; init; }
  public int MutableSegmentMaxItemCount { get; init; } = 250_000;
  public string Directory { get; init; }

  public long KeysPerColumn => Keys / Columns;
}

/// <summary>The transaction mixes.</summary>
static class Workload
{
  public const string Write = "write";
  public const string ReadModifyWrite = "rmw";
  public const string Read = "read";
  public const string Mixed = "mixed";
  public const string Disjoint = "disjoint";

  public static readonly string[] Names = [Write, ReadModifyWrite, Read, Mixed, Disjoint];

  public static string Describe(string name) => name switch
  {
    Write => "blind upserts, one key in each of `span` columns",
    ReadModifyWrite => "read then upsert one key in each of `span` columns",
    Read => "read-only transactions, one key in each of `span` columns",
    Mixed => "90% read-only, 10% read-modify-write",
    Disjoint => "blind upserts; worker w writes columns from w*span on, so workers rarely share a column",
    _ => throw new ArgumentException($"Unknown workload '{name}'.")
  };
}

sealed record CellResult(string Engine, string Workload, int Span, int Threads,
    double Seconds, long Writes, long Conflicts, long Reads, long P50Ticks, long P99Ticks,
    long AllocatedBytes, TimeSpan GcPause)
{
  /// <summary>Bytes the process allocated per transaction, background work included.</summary>
  public double BytesPerTransaction => Writes + Conflicts + Reads == 0 ? 0 : (double)AllocatedBytes / (Writes + Conflicts + Reads);
  public double GcPauseShare => GcPause.TotalSeconds / Seconds;
  public double TransactionsPerSecond => (Writes + Reads) / Seconds;
  public double OperationsPerSecond => TransactionsPerSecond * Span;
  public double ConflictRate => Writes + Conflicts == 0 ? 0 : (double)Conflicts / (Writes + Conflicts);
}

static class Program
{
  static int Main(string[] args)
  {
    BenchmarkOptions options;
    try
    {
      options = Parse(args);
      if (options == null)
        return 0;
    }
    catch (Exception error) when (error is ArgumentException or FormatException)
    {
      Console.Error.WriteLine(error.Message);
      Console.Error.WriteLine("Use --help for the options.");
      return 1;
    }

    PrintHeader(options);
    var results = new List<CellResult>();
    foreach (var engineName in options.Engines)
    {
      var directory = Path.Combine(options.Directory, engineName);
      DeleteDirectory(directory);
      try
      {
        using var engine = EngineCatalog.Open(engineName, directory, options);
        var preload = Stopwatch.StartNew();
        Preload(engine, options);
        engine.Settle();
        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{engineName}: preloaded {Count(options.Keys)} records in {preload.Elapsed.TotalSeconds:F1}s"));
        PrintCellHeader();
        foreach (var workload in options.Workloads)
          foreach (var span in options.Spans)
            foreach (var threads in options.Threads)
            {
              // Every cell starts with its records on disk and no merge running, so no
              // cell inherits the background work of the one before it.
              engine.Settle();
              var result = RunCell(engine, engineName, workload, span, threads, options);
              results.Add(result);
              PrintCell(result);
            }
      }
      finally
      {
        DeleteDirectory(directory);
      }
    }
    PrintSummary(results, options);
    return 0;
  }

  static void Preload(IEngine engine, BenchmarkOptions options)
  {
    const int Batch = 10_000;
    var value = new byte[options.ValueSize];
    Parallel.For(0, options.Columns, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(options.Columns, Environment.ProcessorCount) },
        column =>
        {
          var worker = engine.CreateWorker();
          for (var from = 0L; from < options.KeysPerColumn; from += Batch)
          {
            var to = Math.Min(from + Batch, options.KeysPerColumn);
            worker.BeginWrite();
            for (var key = from; key < to; ++key)
              worker.Put(column, key, value);
            if (!worker.Commit())
              throw new InvalidOperationException("A preload transaction conflicted.");
          }
        });
  }

  static CellResult RunCell(IEngine engine, string engineName, string workload, int span, int threads,
      BenchmarkOptions options)
  {
    // 0: warming up, 1: measuring, 2: stopping.
    var phase = 0;
    var value = new byte[options.ValueSize];
    var counters = new WorkerCounters[threads];
    var errors = new List<Exception>();
    var workers = new Thread[threads];
    using var started = new CountdownEvent(threads);
    using var go = new ManualResetEventSlim(false);
    for (var index = 0; index < threads; ++index)
    {
      var workerIndex = index;
      var worker = engine.CreateWorker();
      var counter = counters[workerIndex] = new WorkerCounters();
      workers[index] = new Thread(() =>
      {
        try
        {
          var random = new FastRandom((ulong)(workerIndex + 1) * 0x9E3779B97F4A7C15UL ^ (ulong)span);
          started.Signal();
          go.Wait();
          while (true)
          {
            var current = Volatile.Read(ref phase);
            if (current == 2)
              break;
            var start = Stopwatch.GetTimestamp();
            var outcome = Execute(worker, workload, span, workerIndex, ref random, value, options);
            if (current == 1)
              counter.Record(outcome, Stopwatch.GetTimestamp() - start);
          }
        }
        catch (Exception error)
        {
          lock (errors)
            errors.Add(error);
          Volatile.Write(ref phase, 2);
        }
      })
      { IsBackground = true, Name = $"bench worker {index}" };
      workers[index].Start();
    }
    started.Wait();
    go.Set();
    Thread.Sleep(TimeSpan.FromSeconds(options.WarmupSeconds));
    var allocatedBefore = GC.GetTotalAllocatedBytes(true);
    var pauseBefore = GC.GetTotalPauseDuration();
    var measured = Stopwatch.StartNew();
    Interlocked.CompareExchange(ref phase, 1, 0);
    Thread.Sleep(TimeSpan.FromSeconds(options.Seconds));
    Interlocked.CompareExchange(ref phase, 2, 1);
    var seconds = measured.Elapsed.TotalSeconds;
    var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
    var pause = GC.GetTotalPauseDuration() - pauseBefore;
    foreach (var worker in workers)
      worker.Join();
    if (errors.Count != 0)
      throw new AggregateException($"{engineName} {workload} failed.", errors);

    var histogram = new long[LatencyBuckets.Count];
    foreach (var counter in counters)
      for (var bucket = 0; bucket < histogram.Length; ++bucket)
        histogram[bucket] += counter.Histogram[bucket];
    return new CellResult(engineName, workload, span, threads, seconds,
        counters.Sum(counter => counter.Writes),
        counters.Sum(counter => counter.Conflicts),
        counters.Sum(counter => counter.Reads),
        LatencyBuckets.Percentile(histogram, 0.50),
        LatencyBuckets.Percentile(histogram, 0.99),
        allocated, pause);
  }

  /// <summary>Runs one transaction. Returns its outcome.</summary>
  static Outcome Execute(IEngineWorker worker, string workload, int span, int workerIndex,
      ref FastRandom random, byte[] value, BenchmarkOptions options)
  {
    var columns = options.Columns;
    var keys = options.KeysPerColumn;
    switch (workload)
    {
      case Workload.Write:
      case Workload.Disjoint:
        {
          var first = workload == Workload.Disjoint
              ? workerIndex * span % columns
              : (int)random.Next((ulong)columns);
          worker.BeginWrite();
          for (var i = 0; i < span; ++i)
            worker.Put((first + i) % columns, (long)random.Next((ulong)keys), value);
          return worker.Commit() ? Outcome.Committed : Outcome.Conflict;
        }
      case Workload.ReadModifyWrite:
        return ReadModifyWrite(worker, span, ref random, value, columns, keys);
      case Workload.Read:
        return Read(worker, span, ref random, columns, keys);
      case Workload.Mixed:
        return random.Next(10) == 0
            ? ReadModifyWrite(worker, span, ref random, value, columns, keys)
            : Read(worker, span, ref random, columns, keys);
      default:
        throw new ArgumentException($"Unknown workload '{workload}'.");
    }
  }

  static Outcome ReadModifyWrite(IEngineWorker worker, int span, ref FastRandom random, byte[] value,
      int columns, long keys)
  {
    var first = (int)random.Next((ulong)columns);
    worker.BeginWrite();
    for (var i = 0; i < span; ++i)
    {
      var column = (first + i) % columns;
      var key = (long)random.Next((ulong)keys);
      worker.Get(column, key, out _);
      worker.Put(column, key, value);
    }
    return worker.Commit() ? Outcome.Committed : Outcome.Conflict;
  }

  static Outcome Read(IEngineWorker worker, int span, ref FastRandom random, int columns, long keys)
  {
    var first = (int)random.Next((ulong)columns);
    worker.BeginRead();
    try
    {
      for (var i = 0; i < span; ++i)
        if (!worker.Read((first + i) % columns, (long)random.Next((ulong)keys), out _))
          throw new InvalidOperationException("A preloaded key is missing.");
    }
    finally
    {
      worker.EndRead();
    }
    return Outcome.Read;
  }

  enum Outcome { Committed, Conflict, Read }

  sealed class WorkerCounters
  {
    public long Writes;
    public long Conflicts;
    public long Reads;
    public readonly long[] Histogram = new long[LatencyBuckets.Count];

    public void Record(Outcome outcome, long ticks)
    {
      switch (outcome)
      {
        case Outcome.Committed: ++Writes; break;
        case Outcome.Conflict: ++Conflicts; return;
        default: ++Reads; break;
      }
      ++Histogram[LatencyBuckets.Index(ticks)];
    }
  }

  /// <summary>Log-linear latency buckets: 8 per power of two, so about 12% resolution.</summary>
  static class LatencyBuckets
  {
    public const int Count = 512;

    public static int Index(long ticks)
    {
      if (ticks < 16)
        return (int)Math.Max(ticks, 0);
      var log = BitOperations.Log2((ulong)ticks);
      return Math.Min(Count - 1, 16 + (log - 4) * 8 + (int)((ticks >> (log - 3)) & 7));
    }

    static long LowerBound(int index)
    {
      if (index < 16)
        return index;
      var log = (index - 16) / 8 + 4;
      var sub = (index - 16) % 8;
      return (8L + sub) << (log - 3);
    }

    public static long Percentile(long[] histogram, double percentile)
    {
      var total = histogram.Sum();
      if (total == 0)
        return 0;
      var target = (long)Math.Ceiling(total * percentile);
      var seen = 0L;
      for (var index = 0; index < histogram.Length; ++index)
      {
        seen += histogram[index];
        if (seen >= target)
          return LowerBound(index);
      }
      return LowerBound(histogram.Length - 1);
    }
  }

  /// <summary>xorshift64*: cheap enough not to show in the measurements.</summary>
  struct FastRandom(ulong seed)
  {
    ulong state = seed == 0 ? 0x2545F4914F6CDD1DUL : seed;

    public ulong Next(ulong bound)
    {
      state ^= state >> 12;
      state ^= state << 25;
      state ^= state >> 27;
      return (state * 0x2545F4914F6CDD1DUL) % bound;
    }
  }

  static void PrintHeader(BenchmarkOptions options)
  {
    var cells = options.Engines.Length * options.Workloads.Length * options.Spans.Length * options.Threads.Length;
    var minutes = cells * (options.Seconds + options.WarmupSeconds) / 60;
    Console.WriteLine("Domain transactions benchmark");
    Console.WriteLine($"  {Environment.ProcessorCount} logical cores, {Environment.OSVersion}, .NET {Environment.Version}, server GC: {GCSettings.IsServerGC}");
    Console.WriteLine($"  records: {Count(options.Keys)} across {options.Columns} logical columns ({Count(options.KeysPerColumn)} each), values: {options.ValueSize} bytes");
    Console.WriteLine($"  isolation: {options.Isolation}, WAL: {options.WalMode}, commits: {(options.Durable ? "durable" : "ordinary")}");
    Console.WriteLine($"  memory budget per engine: {Count(options.MutableSegmentMaxItemCount)} mutable records and {EngineCatalog.CacheSize} cached keys and values per segment, {(options.PerTreeBudget ? "for every tree" : "split evenly across the engine's trees")}");
    Console.WriteLine("  each cell starts settled (records on disk, no merge running); settling is not measured");
    Console.WriteLine($"  spans: {string.Join(", ", options.Spans)}; threads: {string.Join(", ", options.Threads)}; {options.WarmupSeconds:0.#}s warmup + {options.Seconds:0.#}s per cell");
    Console.WriteLine($"  data: {options.Directory}");
    Console.WriteLine("  engines:");
    foreach (var engine in options.Engines)
      Console.WriteLine($"    {engine,-20} {EngineCatalog.Describe(engine)}");
    Console.WriteLine("  workloads:");
    foreach (var workload in options.Workloads)
      Console.WriteLine($"    {workload,-20} {Workload.Describe(workload)}");
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"  {cells} cells: about {minutes:F0} minutes, plus preloading {options.Engines.Length} engines"));
  }

  static void PrintCellHeader()
    => Console.WriteLine($"  {"workload",-9} {"span",4} {"threads",7} {"txn/s",11} {"ops/s",11} {"conflicts",9} {"p50 us",9} {"p99 us",9} {"B/txn",7} {"GC",6}");

  static void PrintCell(CellResult result)
    => Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"  {result.Workload,-9} {result.Span,4} {result.Threads,7} {result.TransactionsPerSecond,11:N0} {result.OperationsPerSecond,11:N0} {result.ConflictRate,9:P1} {Microseconds(result.P50Ticks),9:N1} {Microseconds(result.P99Ticks),9:N1} {result.BytesPerTransaction,7:N0} {result.GcPauseShare,6:P1}"));

  /// <summary>
  /// Transactions per second by thread count, one column per engine, and each cluster engine
  /// relative to the one tree.
  /// </summary>
  static void PrintSummary(List<CellResult> results, BenchmarkOptions options)
  {
    var engines = options.Engines;
    // The first engine is the baseline of the ratios.
    var baseline = engines[0];
    var compared = engines.Skip(1).ToArray();
    Console.WriteLine();
    Console.WriteLine("Summary: transactions per second" +
        (compared.Length != 0 ? $"; x = engine / {baseline}" : ""));
    foreach (var workload in options.Workloads)
      foreach (var span in options.Spans)
      {
        Console.WriteLine();
        Console.WriteLine($"  {workload}, span {span}");
        Console.WriteLine($"  {"threads",7} " + string.Join(" ", engines.Select(engine => $"{engine,20}")) +
            string.Concat(compared.Select(engine => $" {Abbreviate(engine),9}")));
        foreach (var threads in options.Threads)
        {
          CellResult Find(string engine) => results.FirstOrDefault(result =>
              result.Engine == engine && result.Workload == workload && result.Span == span && result.Threads == threads);
          var line = $"  {threads,7} " + string.Join(" ", engines.Select(engine => Find(engine) is { } result
              ? string.Create(CultureInfo.InvariantCulture, $"{result.TransactionsPerSecond,20:N0}")
              : $"{"-",20}"));
          var oneTree = Find(baseline);
          foreach (var engine in compared)
          {
            var result = Find(engine);
            line += result == null || oneTree == null
                ? $" {"-",9}"
                : string.Create(CultureInfo.InvariantCulture,
                    $" {result.TransactionsPerSecond / oneTree.TransactionsPerSecond,8:F2}x");
          }
          Console.WriteLine(line);
        }
      }
  }

  static string Abbreviate(string engine) => engine switch
  {
    EngineCatalog.Trees => "trees/tree",
    EngineCatalog.ClusterOneColumn => "1col/tree",
    EngineCatalog.ClusterColumns => "cols/tree",
    EngineCatalog.ClusterRaw => "cluster/zt",
    EngineCatalog.ClusterStandalone => "stand/tree",
    _ => engine
  };

  static double Microseconds(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

  static string Count(long count) => count switch
  {
    >= 1_000_000 when count % 1_000_000 == 0 => $"{count / 1_000_000}M",
    >= 1_000 when count % 1_000 == 0 => $"{count / 1_000}K",
    _ => count.ToString("N0", CultureInfo.InvariantCulture)
  };

  static void DeleteDirectory(string path)
  {
    if (System.IO.Directory.Exists(path))
      System.IO.Directory.Delete(path, recursive: true);
  }

  const string Usage = """
      Domain transactions benchmark: cluster domain transactions against one MVCC tree that
      encodes the column into its keys.

        --smoke                   a quick check that everything runs
        --keys <n>                records across all columns (default 1M; 250K and 1M work)
        --columns <n>             logical columns (default 8)
        --span <list>             columns each transaction touches (default 1,4)
        --threads <list>          worker threads (default 1,2,4,8,16)
        --workload <list>         write, rmw, read, mixed, disjoint (default all)
        --engine <list>           mvcc-one-tree, mvcc-trees, cluster-one-column, cluster-columns
                                  (default), cluster-standalone, or zonetree-raw, cluster-raw
        --seconds <n>             measured seconds per cell (default 5)
        --warmup <n>              warmup seconds per cell (default 1)
        --value-size <n>          value bytes (default 100)
        --isolation <name>        serializable or snapshot (default serializable)
        --wal <mode>              AsyncShared, Shared, AsyncCompressed, SyncCompressed (default AsyncShared)
        --durable                 commit with TryCommitDurableAsync
        --async                   run the cluster engines through their async API
        --per-tree-budget         give every tree the whole memory budget, not a share
        --mutable-segment <n>     mutable segment records (default 250K)
        --dir <path>              data directory (default <repo>/.tmp/domain-transactions)
      """;

  static BenchmarkOptions Parse(string[] args)
  {
    var options = new BenchmarkOptions();
    for (var index = 0; index < args.Length; ++index)
    {
      var name = args[index];
      string Next() => index + 1 < args.Length
          ? args[++index]
          : throw new ArgumentException($"{name} needs a value.");
      options = name switch
      {
        "--help" or "-h" => null,
        "--smoke" => options with
        {
          Keys = 40_000,
          Spans = [1, 2],
          Threads = [1, 4],
          Seconds = 0.5,
          WarmupSeconds = 0.2
        },
        "--keys" => options with { Keys = ParseCount(Next()) },
        "--columns" => options with { Columns = (int)ParseCount(Next()) },
        "--span" => options with { Spans = ParseList(Next()).Select(item => (int)ParseCount(item)).ToArray() },
        "--threads" => options with { Threads = ParseList(Next()).Select(item => (int)ParseCount(item)).ToArray() },
        "--workload" => options with { Workloads = ParseNames(Next(), Workload.Names, "workload") },
        "--engine" => options with { Engines = ParseNames(Next(), EngineCatalog.AllNames, "engine") },
        "--seconds" => options with { Seconds = double.Parse(Next(), CultureInfo.InvariantCulture) },
        "--warmup" => options with { WarmupSeconds = double.Parse(Next(), CultureInfo.InvariantCulture) },
        "--value-size" => options with { ValueSize = (int)ParseCount(Next()) },
        "--isolation" => options with { Isolation = Enum.Parse<TransactionIsolationLevel>(Next(), ignoreCase: true) },
        "--wal" => options with { WalMode = Enum.Parse<WriteAheadLogMode>(Next(), ignoreCase: true) },
        "--durable" => options with { Durable = true },
        "--async" => options with { AsyncApi = true },
        "--per-tree-budget" => options with { PerTreeBudget = true },
        "--mutable-segment" => options with { MutableSegmentMaxItemCount = (int)ParseCount(Next()) },
        "--dir" => options with { Directory = Next() },
        _ => throw new ArgumentException($"Unknown option '{name}'.")
      };
      if (options == null)
      {
        Console.WriteLine(Usage);
        return null;
      }
    }
    if (options.Columns < 1 || options.Keys < options.Columns)
      throw new ArgumentException("--keys must be at least --columns, and --columns at least 1.");
    if (options.Spans.Any(span => span < 1 || span > options.Columns))
      throw new ArgumentException("Each --span must be between 1 and --columns.");
    if (options.Threads.Any(threads => threads < 1))
      throw new ArgumentException("Each --threads value must be at least 1.");
    return options.Directory != null ? options : options with { Directory = DefaultDirectory() };
  }

  static string DefaultDirectory()
  {
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "ZoneTree.sln")))
      root = root.Parent;
    return Path.Combine(root?.FullName ?? Path.GetTempPath(), ".tmp", "domain-transactions");
  }

  static string[] ParseList(string text)
      => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

  static string[] ParseNames(string text, string[] known, string kind)
  {
    var names = ParseList(text);
    foreach (var name in names)
      if (!known.Contains(name))
        throw new ArgumentException($"Unknown {kind} '{name}'. Known: {string.Join(", ", known)}.");
    return names;
  }

  static long ParseCount(string text)
  {
    var multiplier = 1L;
    var number = text.Trim();
    switch (char.ToUpperInvariant(number[^1]))
    {
      case 'K': multiplier = 1_000; number = number[..^1]; break;
      case 'M': multiplier = 1_000_000; number = number[..^1]; break;
      case 'B': multiplier = 1_000_000_000; number = number[..^1]; break;
    }
    return (long)(double.Parse(number, CultureInfo.InvariantCulture) * multiplier);
  }
}
