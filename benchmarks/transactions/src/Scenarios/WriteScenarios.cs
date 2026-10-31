using Transactions.Benchmark.Engines;
using Transactions.Benchmark.Measurement;

namespace Transactions.Benchmark.Scenarios;

/// <summary>
/// Key layout of the scenarios that insert new keys: above every loaded key.
/// </summary>
static class InsertedKeys
{
  public const long Base = 1L << 62;

  const long SequenceMask = (1L << 54) - 1;
  const long Scramble = unchecked((long)0x9E3779B97F4A7C15);

  /// <summary>
  /// A unique key per worker and sequence. Scrambling spreads it over the key space,
  /// like hashed or random ids.
  /// </summary>
  public static long Scattered(int worker, long sequence) =>
      Base | (((sequence * Scramble) & SequenceMask) << 8) | (uint)(worker & 0xff);

  /// <summary>
  /// A unique key that grows for each worker, like ids from a sequence.
  /// </summary>
  public static long Sequential(int worker, long sequence) =>
      Base | ((long)(worker & 0xffff) << 44) | sequence;
}

static class Rmw
{
  /// <summary>
  /// Adds one to the counter of a key inside the transaction.
  /// </summary>
  public static void Increment(IEngineTransaction transaction, Worker worker, long key)
  {
    var counter = transaction.TryGetForUpdate(key, out var value) ? ValueFactory.Counter(value) : 0;
    transaction.Put(key, worker.NewValue(counter + 1));
  }

  public static void Run(Worker worker, long key)
  {
    while (!worker.IsStopping)
    {
      using var transaction = worker.Session.BeginWrite();
      Increment(transaction, worker, key);
      if (worker.Commit(transaction))
        return;
    }
  }
}

/// <summary>
/// One read-modify-write per transaction, over a key picker.
/// </summary>
public sealed class RmwScenario(
    string id,
    string category,
    string title,
    string useCase,
    Func<ScenarioContext, long> keyCount,
    Func<ScenarioContext, long, KeyPicker> picker,
    long job,
    DurabilityMode durability = DurabilityMode.Relaxed)
    : Scenario(id, category, title, useCase, "The counters add up to the number of commits, so no update was lost.")
{
  const string RoleName = "writers";

  public override DurabilityMode Durability => durability;

  public override void Load(ScenarioContext context) =>
      context.LoadCounters(0, keyCount(context));

  public override IReadOnlyList<Role> CreateRoles(ScenarioContext context)
  {
    var pick = picker(context, keyCount(context));
    return [new(RoleName, context.Workers, worker => Rmw.Run(worker, pick(worker.Random)), context.Job(job))];
  }

  public override void Verify(ScenarioContext context)
  {
    var keys = keyCount(context);
    var (sum, count) = context.ScanSum(0, keys - 1);
    var commits = context.TotalCommits(RoleName);
    context.Check(count == keys, $"expected {keys} keys, found {count}");
    context.Check(sum == commits, $"counters add up to {sum}, but {commits} transactions committed");
  }
}

/// <summary>
/// Blind inserts of new keys, one or many per transaction.
/// </summary>
public sealed class InsertScenario(
    string id,
    string category,
    string title,
    string useCase,
    int batchSize,
    bool scattered,
    long job,
    DurabilityMode durability = DurabilityMode.Relaxed)
    : Scenario(id, category, title, useCase, "The inserted keys are exactly the rows of the committed transactions.")
{
  const string RoleName = "writers";

  public override DurabilityMode Durability => durability;

  public override bool ReportsRows => batchSize > 1;

  public override void Load(ScenarioContext context) =>
      context.LoadCounters(0, context.Keys);

  public override IReadOnlyList<Role> CreateRoles(ScenarioContext context) =>
      [new(RoleName, context.Workers, Run, context.Job(job))];

  void Run(Worker worker)
  {
    var first = worker.NextSequence;
    while (!worker.IsStopping)
    {
      using var transaction = worker.Session.BeginWrite();
      for (var i = 0; i < batchSize; i++)
      {
        var sequence = first + i;
        var key = scattered
            ? InsertedKeys.Scattered(worker.Index, sequence)
            : InsertedKeys.Sequential(worker.Index, sequence);
        transaction.Put(key, worker.NewValue(sequence));
      }
      if (worker.Commit(transaction))
      {
        worker.NextSequence = first + batchSize;
        worker.AddRows(batchSize);
        return;
      }
    }
  }

  public override void Verify(ScenarioContext context)
  {
    var (_, inserted) = context.ScanSum(InsertedKeys.Base, long.MaxValue);
    var expected = context.TotalCommits(RoleName) * batchSize;
    context.Check(inserted == expected, $"expected {expected} inserted rows, found {inserted}");
    var (_, loaded) = context.ScanSum(0, context.Keys - 1);
    context.Check(loaded == context.Keys, $"expected {context.Keys} loaded keys, found {loaded}");
  }
}

/// <summary>
/// Moves money between accounts while an auditor checks the total in snapshots.
/// </summary>
public sealed class TransferScenario(bool durable = false)
    : Scenario(
        durable ? "durable-transfer" : "transfer",
        durable ? "durability" : "multi-key",
        durable ? "Durable transfers with an auditor" : "Transfers with an auditor",
        durable
            ? "Payments and ledgers where a transfer must survive a power loss before it is acknowledged."
            : "Payments and ledgers: two keys must change together, and a report must never see money in flight.",
        "The balances keep their total in every audit and after reopening.")
{
  const string Writers = "transfers";
  const string Auditors = "auditor";
  const long InitialBalance = 1_000_000;
  const int MaxAccounts = 10_000;
  const long Job = 200_000;
  const long DurableJob = 10_000;

  public override DurabilityMode Durability => durable ? DurabilityMode.PerCommit : DurabilityMode.Relaxed;

  static long Accounts(ScenarioContext context) => Math.Min(context.Keys, MaxAccounts);

  public override void Load(ScenarioContext context) =>
      context.LoadCounters(0, Accounts(context), InitialBalance);

  public override IReadOnlyList<Role> CreateRoles(ScenarioContext context)
  {
    var accounts = Accounts(context);
    return
    [
      new(Writers, context.Workers, worker => Transfer(worker, accounts), context.Job(durable ? DurableJob : Job)),
      new(Auditors, 1, worker => Audit(worker, accounts)),
    ];
  }

  static void Transfer(Worker worker, long accounts)
  {
    var from = worker.Random.NextInt64(accounts);
    var to = (from + 1 + worker.Random.NextInt64(accounts - 1)) % accounts;
    while (!worker.IsStopping)
    {
      using var transaction = worker.Session.BeginWrite();
      var fromBalance = transaction.TryGetForUpdate(from, out var value) ? ValueFactory.Counter(value) : 0;
      var toBalance = transaction.TryGetForUpdate(to, out value) ? ValueFactory.Counter(value) : 0;
      transaction.Put(from, worker.NewValue(fromBalance - 1));
      transaction.Put(to, worker.NewValue(toBalance + 1));
      if (worker.Commit(transaction))
        return;
    }
  }

  static void Audit(Worker worker, long accounts)
  {
    using var transaction = worker.Session.BeginRead();
    var (sum, count) = ScenarioContext.Scan(transaction, 0, accounts - 1);
    worker.AddRows(count);
    if (sum != accounts * InitialBalance || count != accounts)
      worker.AddAnomalies();
  }

  public override void Verify(ScenarioContext context)
  {
    var accounts = Accounts(context);
    var (sum, count) = context.ScanSum(0, accounts - 1);
    context.Check(count == accounts, $"expected {accounts} accounts, found {count}");
    context.Check(sum == accounts * InitialBalance, $"the balances add up to {sum}, expected {accounts * InitialBalance}");
  }
}

/// <summary>
/// Orders change a customer, three stock items and insert an order row; some requests
/// only list a customer's orders.
/// </summary>
public sealed class OrderEntryScenario(bool durable = false)
    : Scenario(
        durable ? "durable-order-entry" : "order-entry",
        durable ? "durability" : "multi-key",
        durable ? "Durable order entry" : "Order entry",
        durable
            ? "OLTP services where an order must survive a power loss before it is acknowledged."
            : "OLTP services: a mix of multi-key writes on warm rows, inserts, and short range reads.",
        "Customer order counts, order rows and stock sales all match the committed orders.")
{
  const string RoleName = "orders";
  const long ItemBase = 1L << 40;
  const long OrderBase = 1L << 61;
  const int MaxItems = 1_000;
  const int ItemsPerOrder = 3;
  const int RecentOrders = 10;
  const int OrderStatusPercent = 20;
  const long Job = 150_000;
  const long DurableJob = 10_000;

  public override DurabilityMode Durability => durable ? DurabilityMode.PerCommit : DurabilityMode.Relaxed;

  static long Customers(ScenarioContext context) => context.Keys / 2;

  static long Items(ScenarioContext context) => Math.Min(context.Keys / 2, MaxItems);

  static long OrderKey(long customer, long order) => OrderBase + (customer << 24) + order;

  public override void Load(ScenarioContext context)
  {
    context.LoadCounters(0, Customers(context));
    context.LoadCounters(ItemBase, Items(context));
  }

  public override IReadOnlyList<Role> CreateRoles(ScenarioContext context)
  {
    var customers = Customers(context);
    var items = Items(context);
    return
    [
      new(RoleName, context.Workers, worker =>
      {
        if (worker.Random.Next(100) < OrderStatusPercent)
          OrderStatus(worker, worker.Random.NextInt64(customers));
        else
          NewOrder(worker, worker.Random.NextInt64(customers), items);
      }, context.Job(durable ? DurableJob : Job))
    ];
  }

  static void NewOrder(Worker worker, long customer, long items)
  {
    var firstItem = worker.Random.NextInt64(items);
    var secondItem = worker.Random.NextInt64(items);
    var thirdItem = worker.Random.NextInt64(items);
    while (!worker.IsStopping)
    {
      using var transaction = worker.Session.BeginWrite();
      var orders = transaction.TryGetForUpdate(customer, out var value) ? ValueFactory.Counter(value) : 0;
      transaction.Put(customer, worker.NewValue(orders + 1));
      Rmw.Increment(transaction, worker, ItemBase + firstItem);
      Rmw.Increment(transaction, worker, ItemBase + secondItem);
      Rmw.Increment(transaction, worker, ItemBase + thirdItem);
      transaction.Put(OrderKey(customer, orders), worker.NewValue(customer));
      if (worker.Commit(transaction))
      {
        worker.AddRows(ItemsPerOrder + 2);
        return;
      }
    }
  }

  static void OrderStatus(Worker worker, long customer)
  {
    using var transaction = worker.Session.BeginRead();
    if (!transaction.TryGet(customer, out var value))
      return;
    var orders = ValueFactory.Counter(value);
    var rows = 1;
    foreach (var _ in transaction.Range(OrderKey(customer, Math.Max(0, orders - RecentOrders)), OrderKey(customer, orders)))
      ++rows;
    worker.AddRows(rows);
  }

  public override void Verify(ScenarioContext context)
  {
    var commits = context.TotalCommits(RoleName);
    var (customerOrders, _) = context.ScanSum(0, Customers(context) - 1);
    var (sold, _) = context.ScanSum(ItemBase, ItemBase + Items(context) - 1);
    var (_, orderRows) = context.ScanSum(OrderBase, long.MaxValue);
    context.Check(customerOrders == commits, $"customers count {customerOrders} orders, but {commits} committed");
    context.Check(orderRows == commits, $"found {orderRows} order rows, but {commits} committed");
    context.Check(sold == commits * ItemsPerOrder, $"stock sold {sold}, expected {commits * ItemsPerOrder}");
  }
}

/// <summary>
/// Transactions of several reads and a few writes on a skewed key space.
/// </summary>
public sealed class ReadWriteMixScenario(
    string id,
    string title,
    string useCase,
    int readOnlyPercent,
    int readsPerWriteTransaction,
    int writesPerWriteTransaction,
    long job)
    : Scenario(id, "mixed", title, useCase, "The counters add up to the committed writes.")
{
  const string RoleName = "transactions";

  public override void Load(ScenarioContext context) =>
      context.LoadCounters(0, context.Keys);

  public override IReadOnlyList<Role> CreateRoles(ScenarioContext context)
  {
    var pick = KeyPickers.Zipf(context.Keys, context.Options.ZipfTheta);
    return
    [
      new(RoleName, context.Workers, worker =>
      {
        if (worker.Random.Next(100) < readOnlyPercent)
          ReadOnly(worker, pick);
        else
          ReadWrite(worker, pick);
      }, context.Job(job))
    ];
  }

  static void ReadOnly(Worker worker, KeyPicker pick)
  {
    using var transaction = worker.Session.BeginRead();
    transaction.TryGet(pick(worker.Random), out _);
    worker.AddRows(1);
  }

  void ReadWrite(Worker worker, KeyPicker pick)
  {
    Span<long> keys = stackalloc long[readsPerWriteTransaction + writesPerWriteTransaction];
    for (var i = 0; i < keys.Length; i++)
      keys[i] = pick(worker.Random);

    while (!worker.IsStopping)
    {
      using var transaction = worker.Session.BeginWrite();
      for (var i = 0; i < readsPerWriteTransaction; i++)
        transaction.TryGet(keys[i], out _);
      for (var i = readsPerWriteTransaction; i < keys.Length; i++)
        Rmw.Increment(transaction, worker, keys[i]);
      if (worker.Commit(transaction))
      {
        worker.AddRows(readsPerWriteTransaction + writesPerWriteTransaction);
        return;
      }
    }
  }

  public override void Verify(ScenarioContext context)
  {
    var (sum, _) = context.ScanSum(0, context.Keys - 1);
    var expected = context.TotalCommits(RoleName) * writesPerWriteTransaction;
    context.Check(sum == expected, $"counters add up to {sum}, expected {expected}");
  }
}

/// <summary>
/// Transactions that end in a rollback, always or sometimes.
/// </summary>
public sealed class RollbackScenario(string id, string title, string useCase, int rollbackPercent, long job)
    : Scenario(id, "failure", title, useCase, "The counters add up to the committed writes only: rolled back writes left no trace.")
{
  const string RoleName = "writers";
  const int WritesPerTransaction = 5;

  public override void Load(ScenarioContext context) =>
      context.LoadCounters(0, context.Keys);

  public override IReadOnlyList<Role> CreateRoles(ScenarioContext context) =>
      [new(RoleName, context.Workers, worker => Run(worker, context.Keys), context.Job(job))];

  void Run(Worker worker, long keyCount)
  {
    var rollback = worker.Random.Next(100) < rollbackPercent;
    Span<long> keys = stackalloc long[WritesPerTransaction];
    for (var i = 0; i < keys.Length; i++)
      keys[i] = worker.Random.NextInt64(keyCount);

    while (!worker.IsStopping)
    {
      using var transaction = worker.Session.BeginWrite();
      foreach (var key in keys)
        Rmw.Increment(transaction, worker, key);
      if (rollback)
      {
        worker.Rollback(transaction);
        return;
      }
      if (worker.Commit(transaction))
        return;
    }
  }

  public override void Verify(ScenarioContext context)
  {
    var (sum, count) = context.ScanSum(0, context.Keys - 1);
    var expected = context.TotalCommits(RoleName) * WritesPerTransaction;
    context.Check(count == context.Keys, $"expected {context.Keys} keys, found {count}");
    context.Check(sum == expected, $"counters add up to {sum}, expected {expected}");
  }
}
