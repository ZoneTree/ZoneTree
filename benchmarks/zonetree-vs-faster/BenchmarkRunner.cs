using System.Diagnostics;
using System.Globalization;

namespace ZoneTree.FasterBench;

public sealed record PhaseResult(
    string Engine,
    int Parallelism,
    int Iteration,
    string Phase,
    double ElapsedMilliseconds,
    double OperationsPerSecond,
    ulong Checksum);

public sealed record InsertCompletionResult(
    string Engine,
    int Parallelism,
    int Iteration,
    string Phase,
    double ElapsedMilliseconds);

public sealed class BenchmarkRunner(BenchmarkConfig config)
{
  readonly long[] LookupKeys = CreateLookupKeys(config.Records, config.Reads, config.Seed);

  readonly List<PhaseResult> Results = [];

  readonly List<InsertCompletionResult> InsertCompletionResults = [];

  public void Run()
  {
    Directory.CreateDirectory(config.DataDirectory);
    PrintHeader();

    foreach (var parallelism in config.Parallelism)
    {
      foreach (var engineName in config.Engines)
        if (config.WarmupRecords > 0)
          WarmUp(engineName, parallelism);

      for (var iteration = 1; iteration <= config.Iterations; ++iteration)
      {
        var engineOrder = iteration % 2 == 1
            ? config.Engines.AsEnumerable()
            : config.Engines.Reverse();
        foreach (var engineName in engineOrder)
          RunIteration(engineName, parallelism, iteration);
      }
    }

    PrintSummary();
  }

  void WarmUp(string engineName, int parallelism)
  {
    var count = Math.Min(config.Records, config.WarmupRecords);
    var path = PreparePath($"warmup-{engineName}-p{parallelism}");
    using var engine = CreateEngine(engineName, path);
    RunParallel(
        engine,
        parallelism,
        count,
        (worker, start, operationCount) =>
        {
          for (var i = start; i < start + operationCount; ++i)
          {
            var key = KeyForOrdinal(i, config.Seed);
            worker.Insert(key, ValueFor(key));
          }
          return 0;
        });
    engine.CompleteInserts();
    RunParallel(
        engine,
        parallelism,
        count,
        (worker, start, operationCount) =>
        {
          ulong checksum = 0;
          for (var i = start; i < start + operationCount; ++i)
          {
            var key = KeyForOrdinal(i, config.Seed);
            if (!worker.TryGet(key, out var value))
              throw new InvalidOperationException($"Warmup key {key} was not found.");
            checksum += Mix(value);
          }
          return checksum;
        });
  }

  void RunIteration(string engineName, int parallelism, int iteration)
  {
    var path = PreparePath($"{engineName}-p{parallelism}-i{iteration}");
    using var engine = CreateEngine(engineName, path);

    Console.WriteLine();
    Console.WriteLine($"{engine.Name} P{parallelism} iteration {iteration}");

    var insert = Measure(
        engine,
        parallelism,
        iteration,
        "insert",
        config.Records,
        (worker, start, operationCount) =>
        {
          for (var i = start; i < start + operationCount; ++i)
          {
            var key = KeyForOrdinal(i, config.Seed);
            worker.Insert(key, ValueFor(key));
          }
          return 0;
        });
    Results.Add(insert);

    MeasureInsertCompletion(engine, parallelism, iteration);

    var lookup = Measure(
        engine,
        parallelism,
        iteration,
        "random lookup",
        config.Reads,
        (worker, start, operationCount) =>
        {
          ulong checksum = 0;
          for (var i = start; i < start + operationCount; ++i)
          {
            var key = LookupKeys[i];
            if (!worker.TryGet(key, out var value))
              throw new InvalidOperationException($"Key {key} was not found.");
            var expected = ValueFor(key);
            if (value != expected)
              throw new InvalidOperationException(
                  $"Key {key} returned {value}, expected {expected}.");
            checksum += Mix(value);
          }
          return checksum;
        });
    Results.Add(lookup);
  }

  PhaseResult Measure(
      IBenchmarkEngine engine,
      int parallelism,
      int iteration,
      string phase,
      int operations,
      Func<IBenchmarkWorker, int, int, ulong> action)
  {
    Console.Write($"  {phase}... ");
    var (elapsed, checksum) = RunParallel(
        engine,
        parallelism,
        operations,
        action);
    var throughput = operations / elapsed.TotalSeconds;
    Console.WriteLine(
        $"{elapsed.TotalMilliseconds:N1} ms " +
        $"({throughput:N0}/s); checksum={checksum:X16}");
    return new PhaseResult(
        engine.Name,
        parallelism,
        iteration,
        phase,
        elapsed.TotalMilliseconds,
        throughput,
        checksum);
  }

  void MeasureInsertCompletion(
      IBenchmarkEngine engine,
      int parallelism,
      int iteration)
  {
    Console.Write($"  {engine.InsertCompletionPhase}... ");
    var stopwatch = Stopwatch.StartNew();
    engine.CompleteInserts();
    stopwatch.Stop();
    Console.WriteLine($"{stopwatch.Elapsed.TotalMilliseconds:N1} ms");
    InsertCompletionResults.Add(new InsertCompletionResult(
        engine.Name,
        parallelism,
        iteration,
        engine.InsertCompletionPhase,
        stopwatch.Elapsed.TotalMilliseconds));
  }

  static (TimeSpan Elapsed, ulong Checksum) RunParallel(
      IBenchmarkEngine engine,
      int parallelism,
      int operations,
      Func<IBenchmarkWorker, int, int, ulong> action)
  {
    var workers = new IBenchmarkWorker[parallelism];
    for (var i = 0; i < workers.Length; ++i)
      workers[i] = engine.CreateWorker();

    using var ready = new CountdownEvent(parallelism);
    using var startGate = new ManualResetEventSlim(false);
    var tasks = new Task<ulong>[parallelism];
    try
    {
      for (var workerIndex = 0; workerIndex < parallelism; ++workerIndex)
      {
        var index = workerIndex;
        var (start, count) = Partition(operations, index, parallelism);
        tasks[index] = Task.Factory.StartNew(
            () =>
            {
              ready.Signal();
              startGate.Wait();
              return action(workers[index], start, count);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
      }

      ready.Wait();
      var stopwatch = Stopwatch.StartNew();
      startGate.Set();
      Task.WaitAll(tasks);
      stopwatch.Stop();

      ulong checksum = 0;
      foreach (var task in tasks)
        checksum += task.Result;
      return (stopwatch.Elapsed, checksum);
    }
    finally
    {
      foreach (var worker in workers)
        worker?.Dispose();
    }
  }

  IBenchmarkEngine CreateEngine(string name, string path)
  {
    return name switch
    {
      "zonetree" => new ZoneTreeEngine(path),
      "faster" => new FasterEngine(
          Path.Combine(path, "hybrid.log"),
          config.FasterIndexSize,
          config.FasterMemoryBits),
      _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
  }

  string PreparePath(string name)
  {
    var path = Path.Combine(config.DataDirectory, name);
    if (Directory.Exists(path))
      Directory.Delete(path, recursive: true);
    Directory.CreateDirectory(path);
    return path;
  }

  void PrintHeader()
  {
    Console.WriteLine("ZoneTree vs FASTER");
    Console.WriteLine(
        $"records={config.Records:N0}; reads={config.Reads:N0}; " +
        $"parallelism={string.Join(',', config.Parallelism)}; " +
        $"iterations={config.Iterations}; warmup={config.WarmupRecords:N0}");
    Console.WriteLine("keys=unique random Int64; values=Int64; lookups=random successful/hot");
    Console.WriteLine(
        $"FASTER index={config.FasterIndexSize:N0}; " +
        $"hybrid-log memory={1L << config.FasterMemoryBits:N0} bytes");
    Console.WriteLine("ZoneTree WAL=AsyncPrivate; live background compaction enabled");
    Console.WriteLine("FASTER storage=file-backed HybridLog; fold-over checkpoint after inserts");
    Console.WriteLine("insert, write completion, and lookup are measured separately");
    Console.WriteLine($"data={config.DataDirectory}");
  }

  void PrintSummary()
  {
    Console.WriteLine();
    Console.WriteLine("Median throughput");
    Console.WriteLine("Engine     P  Phase                   Median/s       Min/s       Max/s");
    Console.WriteLine("---------- -- -------------------- ------------ ----------- -----------");

    foreach (var group in Results.GroupBy(x => new { x.Engine, x.Parallelism, x.Phase }))
    {
      var values = group.Select(x => x.OperationsPerSecond).Order().ToArray();
      var median = values.Length % 2 == 0
          ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2
          : values[values.Length / 2];
      Console.WriteLine(
          $"{group.Key.Engine,-10} {group.Key.Parallelism,2} " +
          $"{group.Key.Phase,-20} {median,12:N0} {values[0],11:N0} {values[^1],11:N0}");
    }

    Console.WriteLine();
    Console.WriteLine("Median write-completion time");
    Console.WriteLine("Engine     P  Phase                   Median ms      Min ms      Max ms");
    Console.WriteLine("---------- -- -------------------- ------------ ----------- -----------");

    foreach (var group in InsertCompletionResults.GroupBy(
        x => new { x.Engine, x.Parallelism, x.Phase }))
    {
      var values = group.Select(x => x.ElapsedMilliseconds).Order().ToArray();
      var median = values.Length % 2 == 0
          ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2
          : values[values.Length / 2];
      Console.WriteLine(
          $"{group.Key.Engine,-10} {group.Key.Parallelism,2} " +
          $"{group.Key.Phase,-20} {median,12:N1} {values[0],11:N1} {values[^1],11:N1}");
    }
  }

  static (int Start, int Count) Partition(int total, int index, int partitions)
  {
    var baseCount = total / partitions;
    var remainder = total % partitions;
    var count = baseCount + (index < remainder ? 1 : 0);
    var start = index * baseCount + Math.Min(index, remainder);
    return (start, count);
  }

  static long[] CreateLookupKeys(int records, int reads, int seed)
  {
    var random = new Random(seed);
    var keys = new long[reads];
    for (var i = 0; i < keys.Length; ++i)
      keys[i] = KeyForOrdinal(random.NextInt64(records), seed);
    return keys;
  }

  static long KeyForOrdinal(long ordinal, int seed) =>
      unchecked((long)Mix(ordinal + (uint)seed));

  static long ValueFor(long key) =>
      unchecked(key * 6364136223846793005L + 1442695040888963407L);

  static ulong Mix(long value)
  {
    unchecked
    {
      var hash = (ulong)value + 0x9E3779B97F4A7C15UL;
      hash = (hash ^ (hash >> 30)) * 0xBF58476D1CE4E5B9UL;
      hash = (hash ^ (hash >> 27)) * 0x94D049BB133111EBUL;
      return hash ^ (hash >> 31);
    }
  }
}
