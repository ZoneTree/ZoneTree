using System.Globalization;
using static Transactions.Benchmark.Engines.RocksDbNativeApi;

namespace Transactions.Benchmark.Engines;

/// <summary>
/// RocksDB through its C API, as an OptimisticTransactionDB or a TransactionDB.
/// Keys are big-endian so the bytewise order is the numeric order.
/// </summary>
public sealed unsafe class RocksDbEngine : ITransactionEngine
{
  const int BloomBitsPerKey = 10;

  readonly string Directory;
  readonly bool Optimistic;
  readonly nint BlockCache;
  readonly nint Options;
  readonly nint TransactionDbOptions;
  readonly nint WriteOptions;
  readonly nint BeginOptions;

  /// <summary>
  /// The OptimisticTransactionDB or TransactionDB handle.
  /// </summary>
  nint Db;

  /// <summary>
  /// The DB behind an OptimisticTransactionDB. A TransactionDB has its own read functions.
  /// </summary>
  nint BaseDb;

  public IReadOnlyList<string> Settings { get; }

  public RocksDbEngine(EngineOpenOptions options, bool optimistic)
  {
    Directory = options.Directory;
    Optimistic = optimistic;
    var tuning = options.Tuning;
    var writeBufferBytes = (ulong)tuning.RocksDbWriteBufferMb * 1024 * 1024;
    var sync = options.Durability == DurabilityMode.PerCommit;

    Options = rocksdb_options_create();
    rocksdb_options_set_create_if_missing(Options, 1);
    rocksdb_options_increase_parallelism(Options, Environment.ProcessorCount);
    rocksdb_options_optimize_level_style_compaction(Options, writeBufferBytes);
    rocksdb_options_set_write_buffer_size(Options, (nuint)writeBufferBytes);
    rocksdb_options_set_max_write_buffer_number(Options, tuning.RocksDbMaxWriteBuffers);
    rocksdb_options_set_compression(Options, ZstdCompression);

    BlockCache = rocksdb_cache_create_lru((nuint)((ulong)tuning.RocksDbBlockCacheMb * 1024 * 1024));
    var tableOptions = rocksdb_block_based_options_create();
    rocksdb_block_based_options_set_block_cache(tableOptions, BlockCache);
    rocksdb_block_based_options_set_filter_policy(tableOptions, rocksdb_filterpolicy_create_bloom_full(BloomBitsPerKey));
    rocksdb_options_set_block_based_table_factory(Options, tableOptions);
    rocksdb_block_based_options_destroy(tableOptions);

    WriteOptions = rocksdb_writeoptions_create();
    rocksdb_writeoptions_set_sync(WriteOptions, sync ? (byte)1 : (byte)0);

    if (optimistic)
    {
      BeginOptions = rocksdb_optimistictransaction_options_create();
      rocksdb_optimistictransaction_options_set_set_snapshot(BeginOptions, 1);
    }
    else
    {
      TransactionDbOptions = rocksdb_transactiondb_options_create();
      BeginOptions = rocksdb_transaction_options_create();
      rocksdb_transaction_options_set_deadlock_detect(BeginOptions, 1);
      rocksdb_transaction_options_set_lock_timeout(BeginOptions, tuning.RocksDbLockTimeoutMs);
    }
    Open();

    Settings =
    [
      optimistic
          ? "OptimisticTransactionDB; every transaction takes a snapshot and reads it"
          : string.Create(CultureInfo.InvariantCulture, $"TransactionDB; deadlock detection on; lock timeout: {tuning.RocksDbLockTimeoutMs} ms; no snapshot in write transactions"),
      sync ? "WAL: fsync per commit (sync=true)" : "WAL: written per commit, no fsync (sync=false)",
      string.Create(CultureInfo.InvariantCulture, $"Write buffer: {tuning.RocksDbWriteBufferMb} MB x {tuning.RocksDbMaxWriteBuffers}"),
      string.Create(CultureInfo.InvariantCulture, $"Block cache: {tuning.RocksDbBlockCacheMb} MB; bloom filter: {BloomBitsPerKey} bits per key"),
      string.Create(CultureInfo.InvariantCulture, $"Zstd compression; level style compaction; {Environment.ProcessorCount} background threads"),
    ];
  }

  void Open()
  {
    nint error = 0;
    if (Optimistic)
    {
      Db = rocksdb_optimistictransactiondb_open(Options, Directory, ref error);
      ThrowIfError(error);
      BaseDb = rocksdb_optimistictransactiondb_get_base_db(Db);
      return;
    }
    Db = rocksdb_transactiondb_open(Options, TransactionDbOptions, Directory, ref error);
    ThrowIfError(error);
  }

  void Close()
  {
    if (Optimistic)
    {
      rocksdb_optimistictransactiondb_close_base_db(BaseDb);
      rocksdb_optimistictransactiondb_close(Db);
      return;
    }
    rocksdb_transactiondb_close(Db);
  }

  public IEngineSession OpenSession() => new Session(this);

  public void Settle()
  {
    if (!Optimistic)
    {
      // The C API has no flush for a TransactionDB. Opening it writes the WAL to files.
      Close();
      Open();
      return;
    }

    nint error = 0;
    var flushOptions = rocksdb_flushoptions_create();
    rocksdb_flushoptions_set_wait(flushOptions, 1);
    rocksdb_flush(BaseDb, flushOptions, ref error);
    rocksdb_flushoptions_destroy(flushOptions);
    ThrowIfError(error);
  }

  public void Dispose()
  {
    Close();
    rocksdb_writeoptions_destroy(WriteOptions);
    rocksdb_options_destroy(Options);
    rocksdb_cache_destroy(BlockCache);
    if (Optimistic)
    {
      rocksdb_optimistictransaction_options_destroy(BeginOptions);
      return;
    }
    rocksdb_transaction_options_destroy(BeginOptions);
    rocksdb_transactiondb_options_destroy(TransactionDbOptions);
  }

  nint CreateSnapshot() =>
      Optimistic ? rocksdb_create_snapshot(BaseDb) : rocksdb_transactiondb_create_snapshot(Db);

  void ReleaseSnapshot(nint snapshot)
  {
    if (Optimistic)
      rocksdb_release_snapshot(BaseDb, snapshot);
    else
      rocksdb_transactiondb_release_snapshot(Db, snapshot);
  }

  byte* Get(nint readOptions, byte* key, out nuint length, ref nint error) =>
      Optimistic
          ? rocksdb_get(BaseDb, readOptions, key, sizeof(long), out length, ref error)
          : rocksdb_transactiondb_get(Db, readOptions, key, sizeof(long), out length, ref error);

  nint CreateIterator(nint readOptions) =>
      Optimistic
          ? rocksdb_create_iterator(BaseDb, readOptions)
          : rocksdb_transactiondb_create_iterator(Db, readOptions);

  nint BeginTransaction(nint reused) =>
      Optimistic
          ? rocksdb_optimistictransaction_begin(Db, WriteOptions, BeginOptions, reused)
          : rocksdb_transaction_begin(Db, WriteOptions, BeginOptions, reused);

  /// <summary>
  /// The errors that mean another transaction won: lock waits, deadlocks and
  /// commit validation.
  /// </summary>
  static bool IsConflict(string message) =>
      message.StartsWith("Resource busy", StringComparison.Ordinal) ||
      message.StartsWith("Operation timed out", StringComparison.Ordinal) ||
      message.StartsWith("Operation aborted", StringComparison.Ordinal) ||
      message.StartsWith("Operation failed. Try again", StringComparison.Ordinal);

  static IEnumerable<KeyValuePair<long, byte[]>> Scan(nint iterator, long from, long to)
  {
    try
    {
      IteratorSeek(iterator, from);
      while (rocksdb_iter_valid(iterator) != 0)
      {
        var key = IteratorKey(iterator);
        if (key > to)
          yield break;
        yield return new(key, IteratorValue(iterator));
        rocksdb_iter_next(iterator);
      }
    }
    finally
    {
      rocksdb_iter_destroy(iterator);
    }
  }

  sealed class Session(RocksDbEngine engine) : IEngineSession
  {
    readonly nint ReadOptions = rocksdb_readoptions_create();

    /// <summary>
    /// RocksDB reuses a finished transaction's object for the next one.
    /// </summary>
    nint Transaction;

    public IEngineReadTransaction BeginRead()
    {
      var snapshot = engine.CreateSnapshot();
      rocksdb_readoptions_set_snapshot(ReadOptions, snapshot);
      return new ReadTransaction(engine, ReadOptions, snapshot);
    }

    public IEngineTransaction BeginWrite()
    {
      Transaction = engine.BeginTransaction(Transaction);
      if (!engine.Optimistic)
        return new WriteTransaction(Transaction, ReadOptions);

      // The C API allocates a holder for each call, and the options copy what it holds.
      var snapshot = rocksdb_transaction_get_snapshot(Transaction);
      rocksdb_readoptions_set_snapshot(ReadOptions, snapshot);
      rocksdb_free((void*)snapshot);
      return new WriteTransaction(Transaction, ReadOptions);
    }

    public void Dispose()
    {
      if (Transaction != 0)
        rocksdb_transaction_destroy(Transaction);
      rocksdb_readoptions_destroy(ReadOptions);
    }
  }

  sealed class ReadTransaction(RocksDbEngine engine, nint readOptions, nint snapshot) : IEngineReadTransaction
  {
    public bool TryGet(long key, out byte[] value)
    {
      var keyBytes = stackalloc byte[sizeof(long)];
      WriteKey(key, keyBytes);
      nint error = 0;
      var result = engine.Get(readOptions, keyBytes, out var length, ref error);
      ThrowIfError(error);
      if (result == null)
      {
        value = null!;
        return false;
      }
      value = TakeValue(result, length);
      return true;
    }

    public IEnumerable<KeyValuePair<long, byte[]>> Range(long from, long to) =>
        Scan(engine.CreateIterator(readOptions), from, to);

    public void Dispose()
    {
      rocksdb_readoptions_set_snapshot(readOptions, 0);
      engine.ReleaseSnapshot(snapshot);
    }
  }

  sealed class WriteTransaction(nint transaction, nint readOptions) : IEngineTransaction
  {
    bool Active = true;

    public bool HasConflict { get; private set; }

    public bool TryGet(long key, out byte[] value)
    {
      var keyBytes = stackalloc byte[sizeof(long)];
      WriteKey(key, keyBytes);
      nint error = 0;
      var result = rocksdb_transaction_get(transaction, readOptions, keyBytes, sizeof(long), out var length, ref error);
      return TakeRead(result, length, error, out value);
    }

    public bool TryGetForUpdate(long key, out byte[] value)
    {
      var keyBytes = stackalloc byte[sizeof(long)];
      WriteKey(key, keyBytes);
      nint error = 0;
      var result = rocksdb_transaction_get_for_update(transaction, readOptions, keyBytes, sizeof(long), out var length, 1, ref error);
      return TakeRead(result, length, error, out value);
    }

    bool TakeRead(byte* result, nuint length, nint error, out byte[] value)
    {
      value = null!;
      if (error != 0)
      {
        ReportError(error);
        return false;
      }
      if (result == null)
        return false;
      value = TakeValue(result, length);
      return true;
    }

    public IEnumerable<KeyValuePair<long, byte[]>> Range(long from, long to) =>
        Scan(rocksdb_transaction_create_iterator(transaction, readOptions), from, to);

    public void Put(long key, byte[] value)
    {
      if (HasConflict)
        return;
      var keyBytes = stackalloc byte[sizeof(long)];
      WriteKey(key, keyBytes);
      nint error = 0;
      fixed (byte* valueBytes = value)
        rocksdb_transaction_put(transaction, keyBytes, sizeof(long), valueBytes, (nuint)value.Length, ref error);
      if (error != 0)
        ReportError(error);
    }

    public void Delete(long key)
    {
      if (HasConflict)
        return;
      var keyBytes = stackalloc byte[sizeof(long)];
      WriteKey(key, keyBytes);
      nint error = 0;
      rocksdb_transaction_delete(transaction, keyBytes, sizeof(long), ref error);
      if (error != 0)
        ReportError(error);
    }

    public bool TryCommit()
    {
      if (HasConflict)
      {
        Rollback();
        return false;
      }
      nint error = 0;
      rocksdb_transaction_commit(transaction, ref error);
      if (error != 0)
      {
        ReportError(error);
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
      nint error = 0;
      rocksdb_transaction_rollback(transaction, ref error);
      ThrowIfError(error);
    }

    void ReportError(nint error)
    {
      var message = TakeError(error);
      if (!IsConflict(message))
        throw new InvalidOperationException(message);
      HasConflict = true;
    }

    public void Dispose() => Rollback();
  }
}
