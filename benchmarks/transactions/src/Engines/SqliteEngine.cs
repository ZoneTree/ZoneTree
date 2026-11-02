using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Transactions.Benchmark.Engines;

/// <summary>
/// SQLite in WAL mode. Write transactions use BEGIN IMMEDIATE, so they run one at a
/// time and never upgrade a read lock. Readers take their snapshot at the first read.
/// </summary>
public sealed class SqliteEngine : ITransactionEngine
{
  const int BusyTimeoutSeconds = 60;
  const int SqliteBusy = 5;
  const int SqliteLocked = 6;

  readonly string ConnectionString;
  readonly string Pragmas;
  readonly bool Raw;

  public IReadOnlyList<string> Settings { get; }

  public SqliteEngine(EngineOpenOptions options, bool raw = false)
  {
    Raw = raw;
    Directory.CreateDirectory(options.Directory);
    var tuning = options.Tuning;
    var synchronous = options.Durability == DurabilityMode.PerCommit ? "FULL" : "NORMAL";
    ConnectionString = new SqliteConnectionStringBuilder
    {
      DataSource = Path.Combine(options.Directory, "benchmark.db"),
      Pooling = false,
      DefaultTimeout = BusyTimeoutSeconds
    }.ToString();
    Pragmas = string.Create(
        CultureInfo.InvariantCulture,
        $"PRAGMA synchronous={synchronous}; PRAGMA busy_timeout={BusyTimeoutSeconds * 1000}; PRAGMA cache_size=-{tuning.SqliteCacheMb * 1024}; PRAGMA mmap_size={(long)tuning.SqliteMmapMb * 1024 * 1024}; PRAGMA temp_store=MEMORY;");

    using var connection = Connect();
    Execute(connection, "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS kv(key INTEGER PRIMARY KEY, value BLOB NOT NULL);");

    Settings =
    [
      raw ? "Raw: autocommit per statement; no explicit BEGIN/COMMIT/ROLLBACK" :
          "One write transaction at a time (BEGIN IMMEDIATE); deferred read transactions",
      $"Journal: WAL; synchronous={synchronous}",
      string.Create(CultureInfo.InvariantCulture, $"Page cache: {tuning.SqliteCacheMb} MB per connection; mmap: {tuning.SqliteMmapMb} MB"),
      "One connection per worker",
    ];
  }

  SqliteConnection Connect()
  {
    var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    Execute(connection, Pragmas);
    return connection;
  }

  static void Execute(SqliteConnection connection, string sql)
  {
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
  }

  static bool IsBusy(SqliteException exception) =>
      exception.SqliteErrorCode is SqliteBusy or SqliteLocked;

  public IEngineSession OpenSession() => new Session(Connect(), Raw);

  public void Settle()
  {
    using var connection = Connect();
    Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
  }

  public void Dispose()
  {
  }

  sealed class Session : IEngineSession
  {
    readonly SqliteConnection Connection;
    readonly SqliteCommand BeginReadCommand;
    readonly SqliteCommand BeginWriteCommand;
    readonly SqliteCommand CommitCommand;
    readonly SqliteCommand RollbackCommand;
    readonly SqliteCommand GetCommand;
    readonly SqliteCommand PutCommand;
    readonly SqliteCommand DeleteCommand;
    readonly SqliteCommand RangeCommand;
    readonly bool Raw;
    readonly RawOperations Operations;

    public Session(SqliteConnection connection, bool raw)
    {
      Connection = connection;
      Raw = raw;
      Operations = new RawOperations(this);
      BeginReadCommand = Prepare("BEGIN");
      BeginWriteCommand = Prepare("BEGIN IMMEDIATE");
      CommitCommand = Prepare("COMMIT");
      RollbackCommand = Prepare("ROLLBACK");
      GetCommand = Prepare("SELECT value FROM kv WHERE key = $key", ("$key", SqliteType.Integer));
      PutCommand = Prepare(
          "INSERT INTO kv(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
          ("$key", SqliteType.Integer),
          ("$value", SqliteType.Blob));
      DeleteCommand = Prepare("DELETE FROM kv WHERE key = $key", ("$key", SqliteType.Integer));
      RangeCommand = Prepare(
          "SELECT key, value FROM kv WHERE key BETWEEN $from AND $to ORDER BY key",
          ("$from", SqliteType.Integer),
          ("$to", SqliteType.Integer));
    }

    SqliteCommand Prepare(string sql, params (string Name, SqliteType Type)[] parameters)
    {
      var command = Connection.CreateCommand();
      command.CommandText = sql;
      command.CommandTimeout = BusyTimeoutSeconds;
      foreach (var (name, type) in parameters)
        command.Parameters.Add(name, type);
      return command;
    }

    public IEngineReadTransaction BeginRead()
    {
      if (Raw)
        return Operations;
      BeginReadCommand.ExecuteNonQuery();
      return new Transaction(this, isWrite: false);
    }

    public IEngineTransaction BeginWrite()
    {
      if (Raw)
        return Operations;
      try
      {
        BeginWriteCommand.ExecuteNonQuery();
      }
      catch (SqliteException exception) when (IsBusy(exception))
      {
        return new Transaction(this, isWrite: true, conflicted: true);
      }
      return new Transaction(this, isWrite: true);
    }

    public bool TryGet(long key, out byte[] value)
    {
      GetCommand.Parameters[0].Value = key;
      var result = GetCommand.ExecuteScalar();
      value = (result as byte[])!;
      return value != null;
    }

    public void Put(long key, byte[] value)
    {
      PutCommand.Parameters[0].Value = key;
      PutCommand.Parameters[1].Value = value;
      PutCommand.ExecuteNonQuery();
    }

    public void Delete(long key)
    {
      DeleteCommand.Parameters[0].Value = key;
      DeleteCommand.ExecuteNonQuery();
    }

    public IEnumerable<KeyValuePair<long, byte[]>> Range(long from, long to)
    {
      RangeCommand.Parameters[0].Value = from;
      RangeCommand.Parameters[1].Value = to;
      using var reader = RangeCommand.ExecuteReader();
      while (reader.Read())
        yield return new(reader.GetInt64(0), (byte[])reader.GetValue(1));
    }

    public void Commit() => CommitCommand.ExecuteNonQuery();

    public void Rollback() => RollbackCommand.ExecuteNonQuery();

    public void Dispose()
    {
      BeginReadCommand.Dispose();
      BeginWriteCommand.Dispose();
      CommitCommand.Dispose();
      RollbackCommand.Dispose();
      GetCommand.Dispose();
      PutCommand.Dispose();
      DeleteCommand.Dispose();
      RangeCommand.Dispose();
      Connection.Dispose();
    }
  }

  sealed class RawOperations(Session session) : IEngineTransaction
  {
    public bool HasConflict => false;

    public bool TryGet(long key, out byte[] value) => session.TryGet(key, out value);

    public bool TryGetForUpdate(long key, out byte[] value) => TryGet(key, out value);

    public IEnumerable<KeyValuePair<long, byte[]>> Range(long from, long to) => session.Range(from, to);

    public void Put(long key, byte[] value) => session.Put(key, value);

    public void Delete(long key) => session.Delete(key);

    public bool TryCommit() => true;

    public void Rollback() { }

    public void Dispose() { }
  }

  sealed class Transaction(Session session, bool isWrite, bool conflicted = false) : IEngineTransaction
  {
    bool Active = !conflicted;

    public bool HasConflict { get; private set; } = conflicted;

    public bool TryGet(long key, out byte[] value)
    {
      if (Active)
        return session.TryGet(key, out value);
      value = null!;
      return false;
    }

    public bool TryGetForUpdate(long key, out byte[] value) => TryGet(key, out value);

    public IEnumerable<KeyValuePair<long, byte[]>> Range(long from, long to) =>
        Active ? session.Range(from, to) : [];

    public void Put(long key, byte[] value)
    {
      if (Active)
        session.Put(key, value);
    }

    public void Delete(long key)
    {
      if (Active)
        session.Delete(key);
    }

    public bool TryCommit()
    {
      if (!Active)
        return false;
      try
      {
        session.Commit();
      }
      catch (SqliteException exception) when (IsBusy(exception))
      {
        HasConflict = true;
        Rollback();
        return false;
      }
      Active = false;
      return true;
    }

    public void Rollback()
    {
      if (!Active)
        return;
      Active = false;
      session.Rollback();
    }

    public void Dispose()
    {
      if (!Active)
        return;
      // A read transaction has nothing to undo, and COMMIT ends it the same way.
      if (isWrite)
        Rollback();
      else
      {
        Active = false;
        session.Commit();
      }
    }
  }
}
