using System.Globalization;
using System.Text;
using ZoneTree.Cluster;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;

namespace ZoneTree.ClusterCrashWorker;

/// <summary>
/// A bank workload for kill-process crash tests, and the checks of what a kill leaves.
/// </summary>
/// <remarks>
/// <para>
/// Each transfer moves an amount from a checking account to a savings account, records itself
/// in the ledger, and records in the order column how many commits of its process had returned
/// when it began. Every 32nd transfer of a writer commits durably.
/// </para>
/// <para>
/// When a commit returns, its writer appends an acknowledgement to a file of its own before the
/// next transaction begins. The operating system keeps the appended bytes when the process is
/// killed, so the files list exactly the commits that returned, apart from the last line, which
/// a kill can cut.
/// </para>
/// <para>
/// Meanwhile a maintainer rotates and merges the small mutable segments, a schema thread creates
/// and drops a coordinated column, and two threads write a raw and a standalone column.
/// </para>
/// </remarks>
public static class CrashWorkload
{
  public const string Domain = "bank";

  public const int Accounts = 64;

  public const long InitialBalance = 1_000_000;

  const int Writers = 4;

  const int DurableEvery = 32;

  const string SurvivorsFile = "survivors.txt";

  public static ZoneTreeClusterOptions Options(string dataDirectory, WriteAheadLogMode mode)
  {
    static void Small<TKey, TValue>(ZoneTreeFactory<TKey, TValue> storage)
        => storage.SetMutableSegmentMaxItemCount(4096);
    return new()
    {
      DataDirectory = dataDirectory,
      Logger = new SilentLogger(),
      CoordinatedWriteAheadLog = new() { WriteAheadLogMode = mode },
      Domains =
      [
        new DomainDefinition(Domain)
        {
          Columns =
          [
            new CoordinatedColumnDefinition<long, long>("checking") { ConfigureStorage = Small },
            new CoordinatedColumnDefinition<long, long>("savings") { ConfigureStorage = Small },
            new CoordinatedColumnDefinition<long, long>("ledger") { ConfigureStorage = Small },
            new CoordinatedColumnDefinition<long, long>("order") { ConfigureStorage = Small }
          ]
        }
      ],
      Columns =
      [
        new RawColumnDefinition<long, long>("cache") { ConfigureStorage = Small },
        new StandaloneColumnDefinition<long, long>("visits") { ConfigureStorage = Small }
      ]
    };
  }

  /// <summary>
  /// Opens the cluster, checks what the previous processes left, and runs the workload until
  /// the process is killed. Prints FAIL lines for failed checks, READY once the workload runs,
  /// and ERROR lines for unexpected exceptions.
  /// </summary>
  public static int Run(string dataDirectory, WriteAheadLogMode mode, int process, string acknowledgements)
  {
    using var cluster = new ZoneTreeClusterFactory().OpenOrCreateInProcess(Options(dataDirectory, mode));
    var check = Verify(cluster, acknowledgements, mode);
    foreach (var failure in check.Failures)
      Console.WriteLine("FAIL " + failure);
    if (check.Failures.Count > 0)
      return 1;
    WriteSurvivors(acknowledgements, check.Survivors);
    Console.WriteLine("VERIFIED");

    var domain = cluster.GetDomain(Domain);
    if (!check.Seeded)
      Seed(domain);
    using var maintainer = cluster.CreateMaintainer();
    long acknowledged = 0;
    var threads = new List<Thread>();
    for (var writer = 0; writer < Writers; ++writer)
    {
      var id = writer;
      threads.Add(new Thread(() => Guard(() => Transfers(domain, process, id, acknowledgements, ref acknowledged))));
    }
    threads.Add(new Thread(() => Guard(() => Visits(cluster, process))));
    threads.Add(new Thread(() => Guard(() => Cache(cluster))));
    threads.Add(new Thread(() => Guard(() => Schema(cluster, domain))));
    foreach (var thread in threads)
    {
      thread.IsBackground = true;
      thread.Start();
    }
    Console.WriteLine("READY");
    // The test kills the process long before this.
    Thread.Sleep(TimeSpan.FromSeconds(60));
    return 2;
  }

  static void Guard(Action action)
  {
    try { action(); }
    catch (Exception e) { Console.WriteLine("ERROR " + e.ToString().ReplaceLineEndings(" | ")); }
  }

  static long Key(int process, int writer, long sequence) => ((long)process << 40) | ((long)writer << 32) | sequence;

  static int ProcessOf(long key) => (int)(key >> 40);

  static int WriterOf(long key) => (int)((key >> 32) & 0xFF);

  static long SequenceOf(long key) => key & 0xFFFF_FFFF;

  static long Transfer(int from, int to, int amount) => ((long)from << 32) | ((long)to << 16) | (long)amount;

  static void Seed(IClusterDomain domain)
  {
    using var transaction = domain.BeginTransaction();
    var checking = transaction.GetColumn(domain.GetColumn<long, long>("checking"));
    var savings = transaction.GetColumn(domain.GetColumn<long, long>("savings"));
    for (var account = 0; account < Accounts; ++account)
    {
      checking.Upsert(account, InitialBalance);
      savings.Upsert(account, InitialBalance);
    }
    transaction.Commit();
  }

  /// <summary>
  /// Commits one transfer, recording the number of commits of its process that had returned when
  /// it began. Returns false on a conflict.
  /// </summary>
  public static bool TryTransfer(IClusterDomain domain, int process, int writer, long sequence,
      int from, int to, int amount, long watermark, bool durable)
  {
    using var transaction = domain.BeginTransaction();
    try
    {
      var checking = transaction.GetColumn(domain.GetColumn<long, long>("checking"));
      var savings = transaction.GetColumn(domain.GetColumn<long, long>("savings"));
      if (!checking.TryGetForUpdate(from, out var fromBalance) || !savings.TryGetForUpdate(to, out var toBalance))
        throw new InvalidOperationException("An account is missing.");
      checking.Upsert(from, fromBalance - amount);
      savings.Upsert(to, toBalance + amount);
      var key = Key(process, writer, sequence);
      transaction.GetColumn(domain.GetColumn<long, long>("ledger")).Upsert(key, Transfer(from, to, amount));
      transaction.GetColumn(domain.GetColumn<long, long>("order")).Upsert(key, watermark);
      return durable ? transaction.TryCommitDurableAsync().GetAwaiter().GetResult() : transaction.TryCommit();
    }
    catch (TransactionConflictException)
    {
      return false;
    }
  }

  static void Transfers(IClusterDomain domain, int process, int writer, string acknowledgements, ref long acknowledged)
  {
    var random = new Random(process * 64 + writer);
    using var file = new FileStream(AcknowledgementFile(acknowledgements, process, writer),
        FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 0);
    for (var sequence = 0L; ; ++sequence)
    {
      var from = random.Next(Accounts);
      var to = random.Next(Accounts);
      var amount = random.Next(1, 1000);
      var durable = sequence % DurableEvery == DurableEvery - 1;
      while (true)
      {
        // Read before the transaction begins: these commits returned before it.
        var watermark = Interlocked.Read(ref acknowledged);
        if (!TryTransfer(domain, process, writer, sequence, from, to, amount, watermark, durable))
          continue;
        var global = Interlocked.Increment(ref acknowledged);
        file.Write(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{sequence} {global} {(durable ? 'd' : 'o')}\n")));
        break;
      }
    }
  }

  static void Visits(IZoneTreeCluster cluster, int process)
  {
    var visits = cluster.GetStandaloneColumn<long, long>("visits");
    for (var sequence = 0L; ; ++sequence)
    {
      var key = Key(process, 0, sequence);
      using var transaction = visits.BeginTransaction();
      transaction.Upsert(key, sequence);
      transaction.Upsert(~key, sequence);
      transaction.Commit();
    }
  }

  static void Cache(IZoneTreeCluster cluster)
  {
    var cache = cluster.GetRawColumn<long, long>("cache");
    for (var i = 0L; ; ++i)
    {
      var key = i % 50_000;
      cache.Upsert(key, key * 2);
    }
  }

  static void Schema(IZoneTreeCluster cluster, IClusterDomain domain)
  {
    while (true)
    {
      var scratch = cluster.Schema.GetOrCreateColumnAsync(Domain, new CoordinatedColumnDefinition<long, long>("scratch"))
          .AsTask().GetAwaiter().GetResult();
      using (var transaction = domain.BeginTransaction())
      {
        transaction.GetColumn(scratch).Upsert(1, 1);
        transaction.Commit();
      }
      cluster.Schema.DropColumnAsync("scratch").AsTask().GetAwaiter().GetResult();
      Thread.Sleep(20);
    }
  }

  static string AcknowledgementFile(string directory, int process, int writer)
      => Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"acks-{process}-{writer}.txt"));

  /// <summary>What a check found.</summary>
  public sealed class Check
  {
    public List<string> Failures { get; } = [];

    /// <summary>Whether the accounts exist.</summary>
    public bool Seeded { get; set; }

    /// <summary>How many transfers of each process and writer survive.</summary>
    public Dictionary<(int Process, int Writer), long> Survivors { get; } = [];

    internal void Fail(string failure)
    {
      if (Failures.Count < 40)
        Failures.Add(failure);
    }
  }

  readonly record struct Acknowledgement(int Writer, long Sequence, long Global, bool Durable);

  /// <summary>
  /// Checks the state of an opened cluster against the acknowledgements of the earlier processes.
  /// </summary>
  public static Check Verify(IZoneTreeClusterHost cluster, string acknowledgements, WriteAheadLogMode mode)
  {
    var check = new Check();
    var domain = cluster.GetDomain(Domain);
    Dictionary<long, long> checking, savings, ledger, order;
    // One snapshot of the whole domain.
    using (var read = domain.BeginReadOnlyTransaction())
    {
      Dictionary<long, long> ReadColumn(string name)
      {
        var records = new Dictionary<long, long>();
        using var iterator = read.GetColumn(domain.GetColumn<long, long>(name)).CreateIterator();
        while (iterator.Next())
          records[iterator.CurrentKey] = iterator.CurrentValue;
        return records;
      }
      checking = ReadColumn("checking");
      savings = ReadColumn("savings");
      ledger = ReadColumn("ledger");
      order = ReadColumn("order");
    }

    // The seed commits all accounts or none, before any transfer.
    check.Seeded = checking.Count > 0 || savings.Count > 0;
    var complete = Enumerable.Range(0, Accounts).All(account => checking.ContainsKey(account) && savings.ContainsKey(account));
    if (check.Seeded && (!complete || checking.Count != Accounts || savings.Count != Accounts))
      check.Fail($"The seed is partial: {checking.Count} checking and {savings.Count} savings accounts.");
    if (!check.Seeded && ledger.Count > 0)
      check.Fail($"{ledger.Count} transfers survived without the seed.");

    // Every transfer is whole: its ledger and order records, and both balances.
    foreach (var key in ledger.Keys.Where(key => !order.ContainsKey(key)))
      check.Fail($"Transfer {Describe(key)} has a ledger record and no order record.");
    foreach (var key in order.Keys.Where(key => !ledger.ContainsKey(key)))
      check.Fail($"Transfer {Describe(key)} has an order record and no ledger record.");
    if (check.Seeded && complete)
    {
      var expectedChecking = Enumerable.Repeat(InitialBalance, Accounts).ToArray();
      var expectedSavings = Enumerable.Repeat(InitialBalance, Accounts).ToArray();
      foreach (var transfer in ledger.Values)
      {
        var amount = transfer & 0xFFFF;
        expectedChecking[(int)(transfer >> 32)] -= amount;
        expectedSavings[(int)((transfer >> 16) & 0xFFFF)] += amount;
      }
      for (var account = 0; account < Accounts; ++account)
      {
        if (checking[account] != expectedChecking[account])
          check.Fail($"Checking account {account} holds {checking[account]}, and its {ledger.Count} transfers give {expectedChecking[account]}.");
        if (savings[account] != expectedSavings[account])
          check.Fail($"Savings account {account} holds {savings[account]}, and its {ledger.Count} transfers give {expectedSavings[account]}.");
      }
    }

    // A writer commits in sequence, so its surviving transfers are a prefix.
    foreach (var group in ledger.Keys.GroupBy(key => (Process: ProcessOf(key), Writer: WriterOf(key))))
    {
      var sequences = group.Select(SequenceOf).Order().ToArray();
      if (sequences[^1] != sequences.Length - 1)
        check.Fail($"Process {group.Key.Process} writer {group.Key.Writer}: {sequences.Length} transfers survive, up to sequence {sequences[^1]}.");
      check.Survivors[group.Key] = sequences.Length;
    }

    // What an earlier recovery found stays.
    var survivorsPath = Path.Combine(acknowledgements, SurvivorsFile);
    if (File.Exists(survivorsPath))
    {
      foreach (var line in File.ReadAllLines(survivorsPath))
      {
        var parts = line.Split(' ');
        var group = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
        var earlier = long.Parse(parts[2], CultureInfo.InvariantCulture);
        var now = check.Survivors.GetValueOrDefault(group);
        if (now != earlier)
          check.Fail($"Process {group.Item1} writer {group.Item2}: an earlier recovery found {earlier} transfers, this one {now}.");
      }
    }

    foreach (var (process, list) in ReadAcknowledgements(acknowledgements))
    {
      long Survived(int writer) => check.Survivors.GetValueOrDefault((process, writer));
      // Durability: every commit that returned survives a process kill with the Shared
      // journal, and every durable one with any journal.
      foreach (var lost in list.Where(ack => (mode == WriteAheadLogMode.Shared || ack.Durable) && ack.Sequence >= Survived(ack.Writer)))
        check.Fail($"Process {process} writer {lost.Writer}: the {(lost.Durable ? "durable" : "ordinary")} commit of sequence {lost.Sequence} returned and is lost.");
      // One prefix: a transfer that began after a commit returned survives only with it.
      var watermark = ledger.Keys.Where(key => ProcessOf(key) == process).Select(key => order[key]).DefaultIfEmpty(0).Max();
      foreach (var missing in list.Where(ack => ack.Global <= watermark && ack.Sequence >= Survived(ack.Writer)))
        check.Fail($"Process {process} writer {missing.Writer}: the commit of sequence {missing.Sequence} is lost, and a transfer that began after it returned survives.");
    }

    // The standalone column commits its pairs whole.
    using (var visits = cluster.GetStandaloneColumn<long, long>("visits").BeginReadOnlyTransaction())
    {
      var pairs = new Dictionary<long, long>();
      using var iterator = visits.CreateIterator();
      while (iterator.Next())
        pairs[iterator.CurrentKey] = iterator.CurrentValue;
      foreach (var (key, value) in pairs.Where(pair => pair.Key >= 0))
      {
        if (!pairs.TryGetValue(~key, out var other) || other != value)
          check.Fail($"The standalone column holds visit {key} without its pair.");
      }
      if (pairs.Count(pair => pair.Key >= 0) * 2 != pairs.Count)
        check.Fail("The standalone column holds a pair half without its visit.");
    }

    using (var iterator = cluster.GetRawColumn<long, long>("cache").CreateIterator())
    {
      while (iterator.Next())
      {
        if (iterator.CurrentValue != iterator.CurrentKey * 2)
          check.Fail($"The raw column holds {iterator.CurrentValue} for key {iterator.CurrentKey}.");
      }
    }

    // A crash during the scratch column's create or drop leaves it whole or gone.
    var schema = cluster.Schema.DescribeAsync().AsTask().GetAwaiter().GetResult();
    if (schema.Domains.Single(item => item.Name == Domain).ColumnNames.Contains("scratch"))
    {
      using var read = domain.BeginReadOnlyTransaction();
      using var iterator = read.GetColumn(domain.GetColumn<long, long>("scratch")).CreateIterator();
      while (iterator.Next())
      {
        if (iterator.CurrentKey != 1 || iterator.CurrentValue != 1)
          check.Fail($"The scratch column holds {iterator.CurrentValue} for key {iterator.CurrentKey}.");
      }
    }
    return check;
  }

  static string Describe(long key)
      => string.Create(CultureInfo.InvariantCulture, $"{SequenceOf(key)} of process {ProcessOf(key)} writer {WriterOf(key)}");

  static Dictionary<int, List<Acknowledgement>> ReadAcknowledgements(string directory)
  {
    var result = new Dictionary<int, List<Acknowledgement>>();
    foreach (var path in Directory.GetFiles(directory, "acks-*.txt"))
    {
      var name = Path.GetFileNameWithoutExtension(path).Split('-');
      var process = int.Parse(name[1], CultureInfo.InvariantCulture);
      var writer = int.Parse(name[2], CultureInfo.InvariantCulture);
      var lines = File.ReadAllText(path).Split('\n');
      if (!result.TryGetValue(process, out var list))
        result[process] = list = [];
      // The last element follows the last line feed: empty, or a line the kill cut.
      foreach (var line in lines[..^1])
      {
        var parts = line.Split(' ');
        list.Add(new(writer,
            long.Parse(parts[0], CultureInfo.InvariantCulture),
            long.Parse(parts[1], CultureInfo.InvariantCulture),
            parts[2] == "d"));
      }
    }
    return result;
  }

  static void WriteSurvivors(string directory, Dictionary<(int Process, int Writer), long> survivors)
  {
    var path = Path.Combine(directory, SurvivorsFile);
    var temporary = path + ".tmp";
    File.WriteAllLines(temporary, survivors.Select(pair => string.Create(CultureInfo.InvariantCulture,
        $"{pair.Key.Process} {pair.Key.Writer} {pair.Value}")));
    File.Move(temporary, path, overwrite: true);
  }

  sealed class SilentLogger : ILogger
  {
    public LogLevel LogLevel { get; set; } = LogLevel.Error;

    public void LogError(Exception log) { }

    public void LogWarning(object log) { }

    public void LogInfo(object log) { }

    public void LogTrace(object log) { }
  }
}
