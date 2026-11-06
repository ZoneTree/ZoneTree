using ZoneTree;
using ZoneTree.Cluster;
using ZoneTree.Comparers;
using ZoneTree.Options;
using ZoneTree.Serializers;
using ZoneTree.Transactional;

namespace DomainTransactions.Benchmark;

/// <summary>A storage engine under test, with one logical column per column index.</summary>
interface IEngine : IDisposable
{
  /// <summary>Opens the per-thread state that runs transactions. It allocates nothing per call.</summary>
  IEngineWorker CreateWorker();

  /// <summary>Moves the in-memory records to disk and waits for the merges.</summary>
  void Settle();
}

/// <summary>Runs one transaction at a time on one thread.</summary>
interface IEngineWorker
{
  void BeginWrite();
  bool Get(int column, long key, out byte[] value);
  void Put(int column, long key, byte[] value);

  /// <summary>Commits and ends the write transaction. False means a conflict.</summary>
  bool Commit();

  void BeginRead();
  bool Read(int column, long key, out byte[] value);
  void EndRead();
}

/// <summary>The engines, by name.</summary>
static class EngineCatalog
{
  public const string OneTree = "mvcc-one-tree";
  public const string Trees = "mvcc-trees";
  public const string ClusterOneColumn = "cluster-one-column";
  public const string ClusterColumns = "cluster-columns";

  public const string RawTree = "zonetree-raw";
  public const string ClusterRaw = "cluster-raw";
  public const string ClusterStandalone = "cluster-standalone";

  /// <summary>The transactional engines, which run by default.</summary>
  public static readonly string[] Names = [OneTree, Trees, ClusterOneColumn, ClusterColumns];

  /// <summary>Every engine, including the raw ones that are compared with each other.</summary>
  public static readonly string[] AllNames = [.. Names, ClusterStandalone, RawTree, ClusterRaw];

  public static string Describe(string name) => name switch
  {
    OneTree => "ZoneTree MVCC tree; the column id is encoded into the key",
    Trees => "one ZoneTree MVCC tree per logical column; a transaction commits in each tree separately, not atomically",
    RawTree => "a plain ZoneTree, no transactions; the column id is encoded into the key",
    ClusterRaw => "a cluster raw column, no transactions; the column id is encoded into the key",
    ClusterStandalone => "a cluster standalone column (one MVCC tree); the column id is encoded into the key",
    ClusterOneColumn => "cluster domain with one column; the column id is encoded into the key",
    ClusterColumns => "cluster domain with one coordinated column per logical column",
    _ => throw new ArgumentException($"Unknown engine '{name}'.")
  };

  public static IEngine Open(string name, string directory, BenchmarkOptions options) => name switch
  {
    OneTree => new OneTreeEngine(directory, options),
    Trees => new TreesEngine(directory, options),
    RawTree => new RawTreeEngine(directory, options),
    ClusterRaw => new ClusterRawEngine(directory, options),
    ClusterStandalone => new ClusterStandaloneEngine(directory, options),
    ClusterOneColumn => new ClusterEngine(directory, options, separateColumns: false),
    ClusterColumns => new ClusterEngine(directory, options, separateColumns: true),
    _ => throw new ArgumentException($"Unknown engine '{name}'.")
  };

  /// <summary>Encodes a column id into the high bits of a key, for engines with one tree.</summary>
  public static long EncodeKey(int column, long key) => ((long)column << 40) | key;

  /// <summary>The key and value caches of a disk segment in an engine with one tree.</summary>
  public const int CacheSize = 1_024;

  /// <summary>
  /// The storage settings every engine uses, so only the layout differs. An engine with several
  /// trees splits the memory budget among them: the mutable segments and caches of all its
  /// trees together hold what one tree holds.
  /// </summary>
  public static ZoneTreeFactory<long, byte[]> ConfigureStorage(
      ZoneTreeFactory<long, byte[]> factory, BenchmarkOptions options, int trees)
      => ConfigureSegments(factory, options, trees)
          .ConfigureWriteAheadLogOptions(wal => ConfigureJournal(wal, options));

  /// <summary>
  /// The settings of a coordinated cluster column, which takes its journal from
  /// <see cref="Journal"/> at the cluster level.
  /// </summary>
  public static ZoneTreeFactory<long, byte[]> ConfigureSegments(
      ZoneTreeFactory<long, byte[]> factory, BenchmarkOptions options, int trees)
  {
    var share = options.PerTreeBudget ? 1 : trees;
    return factory
        .SetMutableSegmentMaxItemCount(Math.Max(1_000, options.MutableSegmentMaxItemCount / share))
        .ConfigureDiskSegmentOptions(disk =>
        {
          disk.DefaultSparseArrayStepSize = 16;
          disk.KeyCacheSize = Math.Max(64, CacheSize / share);
          disk.ValueCacheSize = Math.Max(64, CacheSize / share);
        });
  }

  /// <summary>The journal of every engine's trees.</summary>
  public static WriteAheadLogOptions Journal(BenchmarkOptions options)
  {
    var wal = new WriteAheadLogOptions();
    ConfigureJournal(wal, options);
    return wal;
  }

  static void ConfigureJournal(WriteAheadLogOptions wal, BenchmarkOptions options)
  {
    wal.CompressionMethod = CompressionMethod.None;
    wal.WriteAheadLogMode = options.WalMode;
    wal.CompressionBlockSize = 1024 * 256;
  }

}

/// <summary>The engine's MVCC tree, with every logical column in one key space.</summary>
sealed class OneTreeEngine : IEngine
{
  readonly ITransactionalZoneTree<long, byte[]> Tree;
  readonly IMaintainer Maintainer;
  readonly BenchmarkOptions Options;

  public OneTreeEngine(string directory, BenchmarkOptions options)
  {
    Options = options;
    var factory = new ZoneTreeFactory<long, byte[]>()
        .SetDataDirectory(directory)
        .SetComparer(new Int64ComparerAscending())
        .SetKeySerializer(new Int64Serializer());
    Tree = EngineCatalog.ConfigureStorage(factory, options, trees: 1).OpenOrCreateTransactional();
    Maintainer = Tree.CreateMaintainer();
  }

  public IEngineWorker CreateWorker() => new Worker(this);

  public void Settle()
  {
    Maintainer.EvictToDisk();
    Maintainer.WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
  }

  public void Dispose()
  {
    Maintainer.WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
    Maintainer.Dispose();
    Tree.Dispose();
  }

  sealed class Worker(OneTreeEngine engine) : IEngineWorker
  {
    ITransaction<long, byte[]> Write;
    IReadTransaction<long, byte[]> ReadOnly;

    public void BeginWrite() => Write = engine.Tree.BeginTransaction(engine.Options.Isolation);

    public bool Get(int column, long key, out byte[] value)
        => Write.TryGet(EngineCatalog.EncodeKey(column, key), out value);

    public void Put(int column, long key, byte[] value)
        => Write.Upsert(EngineCatalog.EncodeKey(column, key), value);

    public bool Commit()
    {
      try
      {
        return engine.Options.Durable
            ? Write.TryCommitDurableAsync().GetAwaiter().GetResult()
            : Write.TryCommit();
      }
      finally
      {
        Write.Dispose();
        Write = null;
      }
    }

    public void BeginRead() => ReadOnly = engine.Tree.BeginReadOnlyTransaction();

    public bool Read(int column, long key, out byte[] value)
        => ReadOnly.TryGet(EngineCatalog.EncodeKey(column, key), out value);

    public void EndRead()
    {
      ReadOnly.Dispose();
      ReadOnly = null;
    }
  }
}

/// <summary>
/// A cluster domain, either with one coordinated column per logical column, or with one
/// column that holds every logical column in one key space.
/// </summary>
sealed class ClusterEngine : IEngine
{
  readonly IZoneTreeClusterHost Cluster;
  readonly IClusterDomain Domain;
  readonly ICoordinatedColumn<long, byte[]>[] Columns;
  readonly IMaintainer Maintainer;
  readonly BenchmarkOptions Options;
  readonly ClusterTransactionOptions TransactionOptions;
  readonly bool SeparateColumns;

  public ClusterEngine(string directory, BenchmarkOptions options, bool separateColumns)
  {
    Options = options;
    SeparateColumns = separateColumns;
    TransactionOptions = new ClusterTransactionOptions { IsolationLevel = options.Isolation };
    var count = separateColumns ? options.Columns : 1;
    var names = Enumerable.Range(0, count).Select(index => $"c{index}").ToArray();
    Cluster = new ZoneTreeClusterFactory().CreateInProcess(new ZoneTreeClusterOptions
    {
      DataDirectory = directory,
      CoordinatedWriteAheadLog = EngineCatalog.Journal(options),
      Domains =
      [
        new DomainDefinition("bench")
        {
          Columns = names
              .Select(name => (CoordinatedColumnDefinition)new CoordinatedColumnDefinition<long, byte[]>(name)
              {
                ConfigureStorage = factory => EngineCatalog.ConfigureSegments(factory, options, count)
              })
              .ToArray()
        }
      ]
    });
    Domain = Cluster.GetDomain("bench");
    Columns = names.Select(name => Domain.GetColumn<long, byte[]>(name)).ToArray();
    Maintainer = Cluster.CreateMaintainer();
  }

  public IEngineWorker CreateWorker() => new Worker(this);

  public void Settle()
  {
    Maintainer.EvictToDisk();
    Maintainer.WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
  }

  public void Dispose()
  {
    try
    {
      Maintainer.WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
      Maintainer.Dispose();
    }
    finally
    {
      Cluster.Dispose();
    }
  }

  sealed class Worker : IEngineWorker
  {
    readonly ClusterEngine Engine;
    readonly IWriteColumn<long, byte[]>[] WriteViews;
    readonly IReadColumn<long, byte[]>[] ReadViews;
    IDomainTransaction Write;
    IDomainReadTransaction ReadOnly;

    public Worker(ClusterEngine engine)
    {
      Engine = engine;
      WriteViews = new IWriteColumn<long, byte[]>[engine.Columns.Length];
      ReadViews = new IReadColumn<long, byte[]>[engine.Columns.Length];
    }

    // One column holds every logical column, or each logical column has its own.
    int Index(int column) => Engine.SeparateColumns ? column : 0;

    long Key(int column, long key) => Engine.SeparateColumns ? key : EngineCatalog.EncodeKey(column, key);

    IWriteColumn<long, byte[]> WriteView(int column)
    {
      var index = Index(column);
      return WriteViews[index] ??= Write.GetColumn(Engine.Columns[index]);
    }

    public void BeginWrite() => Write = Engine.Domain.BeginTransaction(Engine.TransactionOptions);

    public bool Get(int column, long key, out byte[] value)
        => WriteView(column).TryGet(Key(column, key), out value);

    public void Put(int column, long key, byte[] value)
        => WriteView(column).Upsert(Key(column, key), value);

    public bool Commit()
    {
      try
      {
        return Engine.Options.Durable
            ? Write.TryCommitDurableAsync().GetAwaiter().GetResult()
            : Write.TryCommit();
      }
      finally
      {
        Write.Dispose();
        Write = null;
        Array.Clear(WriteViews);
      }
    }

    public void BeginRead() => ReadOnly = Engine.Domain.BeginReadOnlyTransaction();

    public bool Read(int column, long key, out byte[] value)
    {
      var index = Index(column);
      var view = ReadViews[index] ??= ReadOnly.GetColumn(Engine.Columns[index]);
      return view.TryGet(Key(column, key), out value);
    }

    public void EndRead()
    {
      ReadOnly.Dispose();
      ReadOnly = null;
      Array.Clear(ReadViews);
    }
  }
}

/// <summary>
/// One MVCC tree per logical column, outside the cluster. A transaction runs one transaction
/// in each tree it touches and commits them one after another, so it is not atomic across
/// columns: the baseline for what the domain's atomicity costs.
/// </summary>
sealed class TreesEngine : IEngine
{
  readonly ITransactionalZoneTree<long, byte[]>[] Trees;
  readonly IMaintainer[] Maintainers;
  readonly BenchmarkOptions Options;

  public TreesEngine(string directory, BenchmarkOptions options)
  {
    Options = options;
    Trees = new ITransactionalZoneTree<long, byte[]>[options.Columns];
    Maintainers = new IMaintainer[options.Columns];
    for (var column = 0; column < options.Columns; ++column)
    {
      var factory = new ZoneTreeFactory<long, byte[]>()
          .SetDataDirectory(Path.Combine(directory, $"c{column}"))
          .SetComparer(new Int64ComparerAscending())
          .SetKeySerializer(new Int64Serializer());
      Trees[column] = EngineCatalog.ConfigureStorage(factory, options, options.Columns).OpenOrCreateTransactional();
      Maintainers[column] = Trees[column].CreateMaintainer();
    }
  }

  public IEngineWorker CreateWorker() => new Worker(this);

  public void Settle()
  {
    foreach (var maintainer in Maintainers)
      maintainer.EvictToDisk();
    foreach (var maintainer in Maintainers)
      maintainer.WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
  }

  public void Dispose()
  {
    for (var column = 0; column < Trees.Length; ++column)
    {
      Maintainers[column].WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
      Maintainers[column].Dispose();
      Trees[column].Dispose();
    }
  }

  sealed class Worker : IEngineWorker
  {
    readonly TreesEngine Engine;
    readonly ITransaction<long, byte[]>[] Writes;
    readonly IReadTransaction<long, byte[]>[] Reads;
    readonly Task<bool>[] DurableCommits;

    public Worker(TreesEngine engine)
    {
      Engine = engine;
      Writes = new ITransaction<long, byte[]>[engine.Trees.Length];
      Reads = new IReadTransaction<long, byte[]>[engine.Trees.Length];
      DurableCommits = new Task<bool>[engine.Trees.Length];
    }

    ITransaction<long, byte[]> Write(int column)
        => Writes[column] ??= Engine.Trees[column].BeginTransaction(Engine.Options.Isolation);

    public void BeginWrite() { }

    public bool Get(int column, long key, out byte[] value) => Write(column).TryGet(key, out value);

    public void Put(int column, long key, byte[] value) => Write(column).Upsert(key, value);

    public bool Commit()
    {
      var committed = true;
      try
      {
        if (Engine.Options.Durable)
        {
          // The trees sync in parallel, like a domain sync could.
          for (var column = 0; column < Writes.Length; ++column)
            DurableCommits[column] = Writes[column]?.TryCommitDurableAsync();
          for (var column = 0; column < Writes.Length; ++column)
            if (DurableCommits[column] != null)
              committed &= DurableCommits[column].GetAwaiter().GetResult();
        }
        else
        {
          for (var column = 0; column < Writes.Length; ++column)
            if (Writes[column] != null)
              committed &= Writes[column].TryCommit();
        }
        return committed;
      }
      finally
      {
        for (var column = 0; column < Writes.Length; ++column)
        {
          Writes[column]?.Dispose();
          Writes[column] = null;
          DurableCommits[column] = null;
        }
      }
    }

    public void BeginRead() { }

    public bool Read(int column, long key, out byte[] value)
        => (Reads[column] ??= Engine.Trees[column].BeginReadOnlyTransaction()).TryGet(key, out value);

    public void EndRead()
    {
      for (var column = 0; column < Reads.Length; ++column)
      {
        Reads[column]?.Dispose();
        Reads[column] = null;
      }
    }
  }
}

/// <summary>
/// A plain ZoneTree with every logical column in one key space. A "transaction" is its
/// operations, each applied at once.
/// </summary>
sealed class RawTreeEngine : IEngine
{
  readonly IZoneTree<long, byte[]> Tree;
  readonly IMaintainer Maintainer;

  public RawTreeEngine(string directory, BenchmarkOptions options)
  {
    var factory = new ZoneTreeFactory<long, byte[]>()
        .SetDataDirectory(directory)
        .SetComparer(new Int64ComparerAscending())
        .SetKeySerializer(new Int64Serializer());
    Tree = EngineCatalog.ConfigureStorage(factory, options, trees: 1).OpenOrCreate();
    Maintainer = Tree.CreateMaintainer();
  }

  public IEngineWorker CreateWorker() => new Worker(Tree);

  public void Settle()
  {
    Maintainer.EvictToDisk();
    Maintainer.WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
  }

  public void Dispose()
  {
    Maintainer.WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
    Maintainer.Dispose();
    Tree.Dispose();
  }

  sealed class Worker(IZoneTree<long, byte[]> tree) : IEngineWorker
  {
    public void BeginWrite() { }
    public bool Get(int column, long key, out byte[] value) => tree.TryGet(EngineCatalog.EncodeKey(column, key), out value);
    public void Put(int column, long key, byte[] value) => tree.Upsert(EngineCatalog.EncodeKey(column, key), value);
    public bool Commit() => true;
    public void BeginRead() { }
    public bool Read(int column, long key, out byte[] value) => tree.TryGet(EngineCatalog.EncodeKey(column, key), out value);
    public void EndRead() { }
  }
}

/// <summary>
/// A cluster raw column with every logical column in one key space, configured like
/// <see cref="RawTreeEngine"/>.
/// </summary>
sealed class ClusterRawEngine : IEngine
{
  readonly IZoneTreeClusterHost Cluster;
  readonly IRawColumn<long, byte[]> Column;
  readonly IMaintainer Maintainer;

  public ClusterRawEngine(string directory, BenchmarkOptions options)
  {
    Cluster = new ZoneTreeClusterFactory().CreateInProcess(new ZoneTreeClusterOptions
    {
      DataDirectory = directory,
      Columns =
      [
        new RawColumnDefinition<long, byte[]>("raw")
        {
          ConfigureStorage = factory => EngineCatalog.ConfigureStorage(factory, options, trees: 1)
        }
      ]
    });
    Column = Cluster.GetRawColumn<long, byte[]>("raw");
    Maintainer = Cluster.CreateMaintainer();
  }

  public IEngineWorker CreateWorker() => new Worker(Column);

  public void Settle()
  {
    Maintainer.EvictToDisk();
    Maintainer.WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
  }

  public void Dispose()
  {
    try
    {
      Maintainer.WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
      Maintainer.Dispose();
    }
    finally
    {
      Cluster.Dispose();
    }
  }

  sealed class Worker(IRawColumn<long, byte[]> column) : IEngineWorker
  {
    public void BeginWrite() { }
    public bool Get(int column1, long key, out byte[] value) => column.TryGet(EngineCatalog.EncodeKey(column1, key), out value);
    public void Put(int column1, long key, byte[] value) => column.Upsert(EngineCatalog.EncodeKey(column1, key), value);
    public bool Commit() => true;
    public void BeginRead() { }
    public bool Read(int column1, long key, out byte[] value) => column.TryGet(EngineCatalog.EncodeKey(column1, key), out value);
    public void EndRead() { }
  }
}

/// <summary>
/// A cluster standalone column, which wraps one MVCC tree, with every logical column in one
/// key space. It is configured like <see cref="OneTreeEngine"/>.
/// </summary>
sealed class ClusterStandaloneEngine : IEngine
{
  readonly IZoneTreeClusterHost Cluster;
  readonly IStandaloneColumn<long, byte[]> Column;
  readonly IMaintainer Maintainer;
  readonly BenchmarkOptions Options;
  readonly ClusterTransactionOptions TransactionOptions;

  public ClusterStandaloneEngine(string directory, BenchmarkOptions options)
  {
    Options = options;
    TransactionOptions = new ClusterTransactionOptions { IsolationLevel = options.Isolation };
    Cluster = new ZoneTreeClusterFactory().CreateInProcess(new ZoneTreeClusterOptions
    {
      DataDirectory = directory,
      Columns =
      [
        new StandaloneColumnDefinition<long, byte[]>("standalone")
        {
          ConfigureStorage = factory => EngineCatalog.ConfigureStorage(factory, options, trees: 1)
        }
      ]
    });
    Column = Cluster.GetStandaloneColumn<long, byte[]>("standalone");
    Maintainer = Cluster.CreateMaintainer();
  }

  public IEngineWorker CreateWorker() => new Worker(this);

  public void Settle()
  {
    Maintainer.EvictToDisk();
    Maintainer.WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
  }

  public void Dispose()
  {
    try
    {
      Maintainer.WaitForBackgroundThreadsAsync().GetAwaiter().GetResult();
      Maintainer.Dispose();
    }
    finally
    {
      Cluster.Dispose();
    }
  }

  sealed class Worker(ClusterStandaloneEngine engine) : IEngineWorker
  {
    IStandaloneTransaction<long, byte[]> Write;
    IStandaloneReadTransaction<long, byte[]> ReadOnly;

    public void BeginWrite() => Write = engine.Column.BeginTransaction(engine.TransactionOptions);

    public bool Get(int column, long key, out byte[] value)
        => Write.TryGet(EngineCatalog.EncodeKey(column, key), out value);

    public void Put(int column, long key, byte[] value)
        => Write.Upsert(EngineCatalog.EncodeKey(column, key), value);

    public bool Commit()
    {
      try
      {
        return engine.Options.Durable
            ? Write.TryCommitDurableAsync().GetAwaiter().GetResult()
            : Write.TryCommit();
      }
      finally
      {
        Write.Dispose();
        Write = null;
      }
    }

    public void BeginRead() => ReadOnly = engine.Column.BeginReadOnlyTransaction();

    public bool Read(int column, long key, out byte[] value)
        => ReadOnly.TryGet(EngineCatalog.EncodeKey(column, key), out value);

    public void EndRead()
    {
      ReadOnly.Dispose();
      ReadOnly = null;
    }
  }
}
