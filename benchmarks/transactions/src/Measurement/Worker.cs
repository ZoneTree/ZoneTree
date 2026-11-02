using System.Buffers.Binary;
using Transactions.Benchmark.Engines;

namespace Transactions.Benchmark.Measurement;

/// <summary>
/// Stops the workers: the background roles when the job is done, every role when one fails.
/// </summary>
public sealed class RunControl
{
  volatile bool StopRequested;
  Exception? FirstFailure;

  public bool IsStopping => StopRequested;

  public Exception? Failure => Volatile.Read(ref FirstFailure);

  public void RequestStop() => StopRequested = true;

  public void ReportFailure(Exception exception)
  {
    Interlocked.CompareExchange(ref FirstFailure, exception, null);
    RequestStop();
  }
}

/// <summary>
/// Builds values: an 8 byte counter, then filler that compresses about 2:1, like real records.
/// </summary>
public sealed class ValueFactory
{
  const int RandomBlockSize = 1 << 20;

  readonly byte[] RandomBlock = new byte[RandomBlockSize];
  readonly int ValueSize;
  readonly int RandomPartSize;

  public ValueFactory(int valueSize, int seed)
  {
    ValueSize = valueSize;
    RandomPartSize = (valueSize - sizeof(long)) / 2;
    new Random(seed).NextBytes(RandomBlock);
  }

  public byte[] Create(long counter, Random random)
  {
    var value = new byte[ValueSize];
    BinaryPrimitives.WriteInt64LittleEndian(value, counter);
    var offset = random.Next(RandomBlockSize - RandomPartSize);
    Buffer.BlockCopy(RandomBlock, offset, value, sizeof(long), RandomPartSize);
    return value;
  }

  public static long Counter(byte[] value) =>
      BinaryPrimitives.ReadInt64LittleEndian(value);
}

/// <summary>
/// The state of one worker thread.
/// </summary>
public sealed class Worker(
    RunControl control,
    IEngineSession session,
    ValueFactory values,
    bool retryBackoff,
    int index,
    int seed,
    bool analyzeInvariants = true)
{
  const int MaxBackoffShift = 12;

  /// <summary>
  /// Values and backoff use their own generator: a retry draws from it a different number of
  /// times on each engine, and <see cref="Random"/> must give every engine the same work.
  /// </summary>
  readonly Random RetryRandom = new(seed + 1);

  int ConsecutiveConflicts;

  /// <summary>
  /// Unique among all workers of the run, across roles.
  /// </summary>
  public int Index { get; } = index;

  public IEngineSession Session { get; } = session;

  /// <summary>
  /// Decides the work of each operation. An operation draws from it only before its retry
  /// loop, so a worker does the same work in the same order on every engine.
  /// </summary>
  public Random Random { get; } = new(seed);

  public LatencyHistogram Latency { get; } = new();

  public long Operations { get; private set; }

  public long Commits { get; private set; }

  public long Conflicts { get; private set; }

  public long Rollbacks { get; private set; }

  public long Rows { get; private set; }

  public long Anomalies { get; private set; }

  /// <summary>
  /// A per-worker sequence for scenarios that generate new keys.
  /// </summary>
  public long NextSequence { get; set; }

  public bool IsStopping => control.IsStopping;

  public bool AnalyzeInvariants => analyzeInvariants;

  public byte[] NewValue(long counter) => values.Create(counter, RetryRandom);

  /// <summary>
  /// Commits the transaction, or counts the conflict and backs off for the retry.
  /// </summary>
  public bool Commit(IEngineTransaction transaction)
  {
    if (transaction.TryCommit())
    {
      ConsecutiveConflicts = 0;
      ++Commits;
      return true;
    }

    ++Conflicts;
    if (retryBackoff)
    {
      var shift = Math.Min(++ConsecutiveConflicts, MaxBackoffShift);
      Thread.SpinWait(RetryRandom.Next(1 << shift));
    }
    return false;
  }

  public void Rollback(IEngineTransaction transaction)
  {
    transaction.Rollback();
    ++Rollbacks;
  }

  public void AddRows(long count) => Rows += count;

  public void AddAnomalies(long count = 1)
  {
    if (analyzeInvariants)
      Anomalies += count;
  }

  public void CompleteOperation(long ticks)
  {
    ++Operations;
    Latency.Record(ticks);
  }
}
