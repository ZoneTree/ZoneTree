using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Transactions.Benchmark.Engines;

/// <summary>
/// The RocksDB C API. RocksDbSharp's managed classes do not cover transactions.
/// </summary>
static unsafe class RocksDbNativeApi
{
  const string Library = "rocksdb";

  public const int ZstdCompression = 7;

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_options_create();

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_options_destroy(nint options);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_options_set_create_if_missing(nint options, byte value);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_options_increase_parallelism(nint options, int totalThreads);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_options_optimize_level_style_compaction(nint options, ulong memtableMemoryBudget);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_options_set_write_buffer_size(nint options, nuint size);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_options_set_max_write_buffer_number(nint options, int number);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_options_set_compression(nint options, int compression);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_options_set_block_based_table_factory(nint options, nint tableOptions);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_block_based_options_create();

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_block_based_options_destroy(nint options);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_block_based_options_set_block_cache(nint options, nint cache);

  /// <summary>
  /// The table options take ownership of the filter policy.
  /// </summary>
  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_block_based_options_set_filter_policy(nint options, nint filterPolicy);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_filterpolicy_create_bloom_full(int bitsPerKey);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_cache_create_lru(nuint capacity);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_cache_destroy(nint cache);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_writeoptions_create();

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_writeoptions_destroy(nint options);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_writeoptions_set_sync(nint options, byte value);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_readoptions_create();

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_readoptions_destroy(nint options);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_readoptions_set_snapshot(nint options, nint snapshot);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_flushoptions_create();

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_flushoptions_destroy(nint options);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_flushoptions_set_wait(nint options, byte value);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_flush(nint db, nint options, ref nint error);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_create_snapshot(nint db);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_release_snapshot(nint db, nint snapshot);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern byte* rocksdb_get(nint db, nint options, byte* key, nuint keyLength, out nuint valueLength, ref nint error);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_create_iterator(nint db, nint options);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_free(void* pointer);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_iter_destroy(nint iterator);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_iter_seek(nint iterator, byte* key, nuint keyLength);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern byte rocksdb_iter_valid(nint iterator);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_iter_next(nint iterator);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern byte* rocksdb_iter_key(nint iterator, out nuint length);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern byte* rocksdb_iter_value(nint iterator, out nuint length);

  // Optimistic transactions.

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_optimistictransactiondb_open(
      nint options,
      [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
      ref nint error);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_optimistictransactiondb_close(nint db);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_optimistictransactiondb_get_base_db(nint db);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_optimistictransactiondb_close_base_db(nint baseDb);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_optimistictransaction_options_create();

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_optimistictransaction_options_destroy(nint options);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_optimistictransaction_options_set_set_snapshot(nint options, byte value);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_optimistictransaction_begin(nint db, nint writeOptions, nint options, nint oldTransaction);

  // Pessimistic transactions.

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_transactiondb_options_create();

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_transactiondb_options_destroy(nint options);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_transactiondb_open(
      nint options,
      nint transactionDbOptions,
      [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
      ref nint error);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_transactiondb_close(nint db);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_transactiondb_create_snapshot(nint db);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_transactiondb_release_snapshot(nint db, nint snapshot);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern byte* rocksdb_transactiondb_get(nint db, nint options, byte* key, nuint keyLength, out nuint valueLength, ref nint error);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_transactiondb_create_iterator(nint db, nint options);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_transaction_options_create();

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_transaction_options_destroy(nint options);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_transaction_options_set_deadlock_detect(nint options, byte value);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_transaction_options_set_lock_timeout(nint options, long milliseconds);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_transaction_begin(nint db, nint writeOptions, nint options, nint oldTransaction);

  // Both transaction kinds.

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_transaction_destroy(nint transaction);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_transaction_commit(nint transaction, ref nint error);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_transaction_rollback(nint transaction, ref nint error);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_transaction_get_snapshot(nint transaction);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern byte* rocksdb_transaction_get(nint transaction, nint options, byte* key, nuint keyLength, out nuint valueLength, ref nint error);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern byte* rocksdb_transaction_get_for_update(nint transaction, nint options, byte* key, nuint keyLength, out nuint valueLength, byte exclusive, ref nint error);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_transaction_put(nint transaction, byte* key, nuint keyLength, byte* value, nuint valueLength, ref nint error);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern void rocksdb_transaction_delete(nint transaction, byte* key, nuint keyLength, ref nint error);

  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  public static extern nint rocksdb_transaction_create_iterator(nint transaction, nint options);

  /// <summary>
  /// Takes the message of a native error and frees it.
  /// </summary>
  public static string TakeError(nint error)
  {
    var message = Marshal.PtrToStringUTF8(error) ?? "unknown RocksDB error";
    rocksdb_free((void*)error);
    return message;
  }

  public static void ThrowIfError(nint error)
  {
    if (error != 0)
      throw new InvalidOperationException(TakeError(error));
  }

  /// <summary>
  /// Copies a value the native side allocated and frees it.
  /// </summary>
  public static byte[] TakeValue(byte* value, nuint length)
  {
    var bytes = new ReadOnlySpan<byte>(value, checked((int)length)).ToArray();
    rocksdb_free(value);
    return bytes;
  }

  public static void WriteKey(long key, byte* destination) =>
      BinaryPrimitives.WriteInt64BigEndian(new Span<byte>(destination, sizeof(long)), key);

  public static void IteratorSeek(nint iterator, long key)
  {
    var keyBytes = stackalloc byte[sizeof(long)];
    WriteKey(key, keyBytes);
    rocksdb_iter_seek(iterator, keyBytes, sizeof(long));
  }

  public static long IteratorKey(nint iterator)
  {
    var key = rocksdb_iter_key(iterator, out _);
    return BinaryPrimitives.ReadInt64BigEndian(new ReadOnlySpan<byte>(key, sizeof(long)));
  }

  public static byte[] IteratorValue(nint iterator)
  {
    var value = rocksdb_iter_value(iterator, out var length);
    return new ReadOnlySpan<byte>(value, checked((int)length)).ToArray();
  }
}
