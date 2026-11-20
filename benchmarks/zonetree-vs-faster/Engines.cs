using FASTER.core;
using ZoneTree.Logging;

namespace ZoneTree.FasterBench;

public interface IBenchmarkEngine : IDisposable
{
  string Name { get; }

  string InsertCompletionPhase { get; }

  IBenchmarkWorker CreateWorker();

  void CompleteInserts();
}

public interface IBenchmarkWorker : IDisposable
{
  void Insert(long key, long value);

  bool TryGet(long key, out long value);
}

public sealed class ZoneTreeEngine : IBenchmarkEngine
{
  readonly IZoneTree<long, long> Tree;

  readonly IMaintainer Maintainer;

  public ZoneTreeEngine(string path)
  {
    Tree = new ZoneTreeFactory<long, long>()
        .SetDataDirectory(path)
        .SetLogLevel(ZoneTreeLogLevel.Error)
        .ConfigureDiskSegmentOptions(x => x.DefaultSparseArrayStepSize = 16)
        .OpenOrCreate();
    Maintainer = Tree.CreateMaintainer();
  }

  public string Name => "ZoneTree";

  public string InsertCompletionPhase => "evict/compact";

  public IBenchmarkWorker CreateWorker() => new Worker(Tree);

  public void CompleteInserts()
  {
    Maintainer.EvictToDisk();
    Maintainer.WaitForBackgroundThreads();
  }

  public void Dispose()
  {
    Maintainer.WaitForBackgroundThreads();
    Maintainer.Dispose();
    Tree.Dispose();
  }

  sealed class Worker(IZoneTree<long, long> tree) : IBenchmarkWorker
  {
    public void Insert(long key, long value) => tree.Upsert(key, value);

    public bool TryGet(long key, out long value) => tree.TryGet(key, out value);

    public void Dispose()
    {
    }
  }
}

public sealed class FasterEngine : IBenchmarkEngine
{
  readonly IDevice LogDevice;

  readonly FasterKV<long, long> Store;

  public FasterEngine(string path, int indexSize, int memorySizeBits)
  {
    var checkpointDirectory = Path.Combine(
        Path.GetDirectoryName(path)!,
        "checkpoints");
    Directory.CreateDirectory(checkpointDirectory);
    LogDevice = Devices.CreateLogDevice(path, deleteOnClose: true);
    Store = new FasterKV<long, long>(
        indexSize,
        new LogSettings
        {
          LogDevice = LogDevice,
          MemorySizeBits = memorySizeBits,
          PageSizeBits = Math.Min(memorySizeBits - 3, 25),
          SegmentSizeBits = memorySizeBits
        },
        new CheckpointSettings
        {
          CheckpointDir = checkpointDirectory,
          RemoveOutdated = true
        });
  }

  public string Name => "FASTER";

  public string InsertCompletionPhase => "checkpoint";

  public IBenchmarkWorker CreateWorker()
  {
    var functions = new SimpleFunctions<long, long, Empty>();
    var session = Store
        .For<long, long, Empty>(functions)
        .NewSession<SimpleFunctions<long, long, Empty>>();
    return new Worker(session);
  }

  public void CompleteInserts()
  {
    var (success, _) = Store
        .TakeHybridLogCheckpointAsync(CheckpointType.FoldOver)
        .AsTask()
        .GetAwaiter()
        .GetResult();
    if (!success)
      throw new InvalidOperationException("FASTER could not complete its HybridLog checkpoint.");
  }

  public void Dispose()
  {
    Store.Dispose();
    LogDevice.Dispose();
  }

  sealed class Worker(
      ClientSession<
          long,
          long,
          long,
          long,
          Empty,
          SimpleFunctions<long, long, Empty>> session) : IBenchmarkWorker
  {
    public void Insert(long key, long value)
    {
      var status = session.Upsert(ref key, ref value);
      if (status.IsPending && !session.CompletePending(wait: true))
        throw new InvalidOperationException("FASTER did not complete a pending insert.");
      if (status.IsFaulted)
        throw new InvalidOperationException($"FASTER insert failed: {status}");
    }

    public bool TryGet(long key, out long value)
    {
      value = default;
      var status = session.Read(ref key, ref value);
      if (!status.IsPending)
        return status.Found;

      if (!session.CompletePendingWithOutputs(out var outputs, wait: true))
        throw new InvalidOperationException("FASTER did not complete a pending read.");

      using (outputs)
      {
        if (!outputs.Next())
          throw new InvalidOperationException("FASTER completed a read without an output.");
        ref var completed = ref outputs.Current;
        value = completed.Output;
        return completed.Status.Found;
      }
    }

    public void Dispose()
    {
      session.CompletePending(wait: true);
      session.Dispose();
    }
  }
}
