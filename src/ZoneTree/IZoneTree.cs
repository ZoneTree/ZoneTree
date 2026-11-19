using ZoneTree.Collections.BTree;
using ZoneTree.Comparers;
using ZoneTree.Logger;
using ZoneTree.Serializers;

namespace ZoneTree;

/// <summary>
/// The interface for the core functionality of a ZoneTree.
/// </summary>
/// <remarks>
/// Mutating APIs return an operation index (<c>opIndex</c>). The operation
/// index is ZoneTree's producer write sequence for replay, audit, restore,
/// and replication pipelines. Consumers can also keep the latest accepted
/// operation index by key to apply same-key updates idempotently.
///
/// ZoneTree uses a monotonically increasing producer high-water mark to make
/// sure future writes continue with safe operation indexes after restart, WAL
/// replay, or WAL compaction.
/// </remarks>
/// <typeparam name="TKey">The key type</typeparam>
/// <typeparam name="TValue">The value type</typeparam>
public interface IZoneTree<TKey, TValue> : IDisposable
{
  /// <summary>
  /// The key comparer.
  /// </summary>
  public IRefComparer<TKey> Comparer { get; }

  /// <summary>
  /// The key serializer.
  /// </summary>
  public ISerializer<TKey> KeySerializer { get; }

  /// <summary>
  /// The value serializer.
  /// </summary>
  public ISerializer<TValue> ValueSerializer { get; }

  /// <summary>
  /// Checks the existence of the key in the tree.
  /// </summary>
  /// <param name="key">The key of the element.</param>
  /// <returns>true if key is found in tree; otherwise, false</returns>
  bool ContainsKey(in TKey key);

  /// <summary>
  /// Tries to retrieve the value associated with the specified key.
  /// </summary>
  /// <param name="key">The key of the element to retrieve.</param>
  /// <param name="value">The value of the element associated with the key.</param>
  /// <returns>true if the key is found; otherwise, false</returns>
  bool TryGet(in TKey key, out TValue value);

  /// <summary>
  /// Visits the stored records with keys greater than or equal to the given key,
  /// segment by segment from the newest to the oldest, in ascending key order
  /// within each segment. Unlike an iterator, it takes no tree lock and does not
  /// merge the segments: a key can be visited once per segment that holds it,
  /// deleted records are visited, and the newest segment comes first.
  /// </summary>
  /// <remarks>
  /// A segment that moves forward or merges during the visit can be visited twice.
  /// Use it for point lookups that stop early, such as finding the newest version
  /// of a key stored under several composite keys.
  /// </remarks>
  /// <typeparam name="TVisitor">The visitor type.</typeparam>
  /// <param name="key">The first key to visit.</param>
  /// <param name="visitor">The visitor.</param>
  void VisitSegments<TVisitor>(in TKey key, ref TVisitor visitor)
      where TVisitor : struct, ISegmentRecordVisitor<TKey, TValue>;

  /// <summary>
  /// Tries to add the specified key and value to the collection.
  /// </summary>
  /// <remarks>
  /// It checks the key and then writes it, without a lock. When other threads write the key,
  /// take its lock around the call with <see cref="Lock(in TKey)"/>, or two callers can both add it.
  /// </remarks>
  /// <param name="key">The key of the element to add.</param>
  /// <param name="value">The value of the element to add.</param>
  /// <param name="opIndex">The operation index assigned to the successful write.</param>
  /// <returns>true if the key and value were added successfully; otherwise, false if the key already exists.</returns>
  bool TryAdd(in TKey key, in TValue value, out long opIndex);

  /// <summary>
  /// Tries to get the value of the given key and
  /// updates the value using value updater if found any.
  /// </summary>
  /// <remarks>
  /// It reads the key and then writes it, without a lock. When other threads write the key,
  /// take its lock around the call with <see cref="Lock(in TKey)"/>, or a write in between is lost.
  /// </remarks>
  /// <param name="key">The key of the element.</param>
  /// <param name="value">The value of the element associated with the key.</param>
  /// <param name="valueUpdater">The delegate function that updates the value.</param>
  /// <param name="opIndex">The operation index assigned to the successful update.</param>
  /// <returns>true if the key is found; otherwise, false</returns>
  bool TryGetAndUpdate(
      in TKey key,
      out TValue value,
      ValueUpdaterDelegate<TValue> valueUpdater,
      out long opIndex);

  /// <summary>
  /// Adds or updates the specified key/value pair.
  /// </summary>
  /// <remarks>
  /// It is thread-safe and the fastest write: it writes without reading the key, so it needs
  /// no lock. It does not take the key lock either. When callers read the key and write it back
  /// under <see cref="Lock(in TKey)"/>, an upsert of that key without the lock can overwrite
  /// their write or be overwritten by it.
  /// </remarks>
  /// <param name="key">The key of the element to upsert.</param>
  /// <param name="value">The value of the element to upsert.</param>
  /// <returns>The operation index. It can be used to distribute the operations in stable order.</returns>
  long Upsert(in TKey key, in TValue value);

  /// <summary>
  /// Adds or updates the specified key with a value getter.
  /// Value getter receives the operation index as an argument.
  /// It is useful when the user wants to save the operation index into the record.
  /// </summary>
  /// <param name="key">The key of the element to upsert.</param>
  /// <param name="valueGetter">The delegate provides the value to upsert.</param>
  /// <returns>The operation index assigned to the write.</returns>
  long Upsert(in TKey key, GetValueDelegate<TKey, TValue> valueGetter);

  /// <summary>
  /// Attempts to delete the specified key.
  /// </summary>
  /// <remarks>
  /// It checks the key and then deletes it, without a lock. When other threads write the key,
  /// take its lock around the call with <see cref="Lock(in TKey)"/>.
  /// </remarks>
  /// <param name="key">The key of the element to delete.</param>
  /// <param name="opIndex">The operation index assigned to the successful delete marker.</param>
  /// <returns>true if the key was found and deleted;
  /// false if the key was not found.</returns>
  bool TryDelete(in TKey key, out long opIndex);

  /// <summary>
  /// Deletes the specified key regardless of existence. (hint: LSM Tree delete is an insert)
  /// This is faster than TryDelete because it does not check existence in all layers.
  /// It increases the data lake size.
  /// </summary>
  /// <param name="key">The key of the element to delete.</param>
  /// <returns>The operation index assigned to the delete marker.</returns>
  long ForceDelete(in TKey key);

  /// <summary>
  /// Locks the key until the returned scope is disposed.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Take the lock around a write that depends on what it reads, such as an increment:
  /// no other caller that locks the key can write it between the read and the write.
  /// The lock only orders callers that take it. A write of the key without the lock can
  /// still happen in between.
  /// </para>
  /// <para>
  /// The tree has <see cref="ZoneTreeOptions{TKey, TValue}.LockStripeCount"/> locks, and a key
  /// takes the lock of its stripe, which the key hasher chooses. Keys that the comparer
  /// considers equal share a lock, and different keys can share a lock too.
  /// </para>
  /// <para>
  /// The lock is not reentrant: locking a key whose lock the caller already holds waits
  /// forever. Lock several keys with one <see cref="LockMany(ReadOnlySpan{TKey})"/> call
  /// instead. Sync and async callers exclude each other, and the lock can be held across
  /// an await.
  /// </para>
  /// <para>
  /// The locks are semaphores, so an uncontended lock costs about twice a plain lock statement.
  /// When every writer of the keys is synchronous, a plain lock on objects that all writers share
  /// is faster. Plain locks and key locks do not exclude each other: use one of them for all
  /// writers of a key.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// using (tree.Lock(key))
  /// {
  ///   tree.TryGet(key, out var count);
  ///   tree.Upsert(key, count + 1);
  /// }
  /// </code>
  /// </example>
  /// <param name="key">The key to lock.</param>
  /// <returns>The scope that releases the lock when it is disposed.</returns>
  KeyLockScope Lock(in TKey key);

  /// <summary>
  /// Locks the keys until the returned scope is disposed.
  /// </summary>
  /// <remarks>
  /// It takes the lock of each key once, in a fixed order, so callers that lock the same keys
  /// in a different order do not deadlock, and keys that share a lock do not wait for themselves.
  /// See <see cref="Lock(in TKey)"/> for how the locks work.
  /// </remarks>
  /// <param name="keys">The keys to lock.</param>
  /// <returns>The scope that releases the locks when it is disposed.</returns>
  KeyLockScope LockMany(params ReadOnlySpan<TKey> keys);

  /// <summary>
  /// Locks the keys until the returned scope is disposed. C# 13 and later choose
  /// <see cref="LockMany(ReadOnlySpan{TKey})"/>, which needs no array for the keys; earlier
  /// versions call this overload. Either way, holding more than two distinct locks
  /// allocates an array for them.
  /// </summary>
  /// <param name="keys">The keys to lock.</param>
  /// <returns>The scope that releases the locks when it is disposed.</returns>
  /// <exception cref="ArgumentNullException">The keys are null.</exception>
  KeyLockScope LockMany(params TKey[] keys);

  /// <summary>
  /// Locks the key until the returned scope is disposed, if its lock is free within the timeout.
  /// See <see cref="Lock(in TKey)"/> for how the locks work.
  /// </summary>
  /// <param name="key">The key to lock.</param>
  /// <param name="timeout">How long to wait for the lock. Zero does not wait.</param>
  /// <param name="scope">The scope that releases the lock when it is disposed, or the default
  /// value, which holds nothing, if the timeout passed.</param>
  /// <returns>true if the key is locked; otherwise, false.</returns>
  bool TryLock(in TKey key, TimeSpan timeout, out KeyLockScope scope);

  /// <summary>
  /// Locks the key until the returned scope is disposed, without blocking the thread while
  /// it waits. See <see cref="Lock(in TKey)"/> for how the locks work.
  /// </summary>
  /// <remarks>
  /// A released lock goes to a waiting synchronous caller first. On a key that synchronous
  /// callers lock without pause, an asynchronous caller can wait a long time.
  /// </remarks>
  /// <param name="key">The key to lock.</param>
  /// <param name="cancellationToken">Cancels the wait for the lock.</param>
  /// <returns>The scope that releases the lock when it is disposed.</returns>
  /// <exception cref="OperationCanceledException">The wait was cancelled, and nothing is locked.</exception>
  ValueTask<KeyLockScope> LockAsync(TKey key, CancellationToken cancellationToken = default);

  /// <summary>
  /// Locks the keys until the returned scope is disposed, without blocking the thread while
  /// it waits. See <see cref="LockMany(ReadOnlySpan{TKey})"/> for how several keys are locked.
  /// </summary>
  /// <param name="keys">The keys to lock.</param>
  /// <param name="cancellationToken">Cancels the wait for the locks.</param>
  /// <returns>The scope that releases the locks when it is disposed.</returns>
  /// <exception cref="OperationCanceledException">The wait was cancelled, and nothing is locked.</exception>
  /// <exception cref="ArgumentNullException">The keys are null.</exception>
  ValueTask<KeyLockScope> LockManyAsync(IEnumerable<TKey> keys, CancellationToken cancellationToken = default);

  /// <summary>
  /// Counts Keys in the entire database.
  /// This operation scans the in-memory segments and queries the disk segment.
  /// </summary>
  /// <returns>Number of the valid records in the tree.</returns>
  long Count();

  /// <summary>
  /// Counts Keys in the entire database with a full scan.
  /// </summary>
  /// <remarks>
  /// In regular cases, the disk segment does not contain deleted records.
  /// However, TTL or custom deletion logic would let the disk segment
  /// contains deleted records. In that case, a full scan is required for the count.
  /// </remarks>
  /// <returns>Number of the valid records in the tree.</returns>
  long CountFullScan();

  /// <summary>
  /// Creates an iterator that enables scanning of the entire database.
  /// </summary>
  ///
  /// <remarks>
  /// The iterator might or might not retrieve newly inserted elements.
  /// This depends on the iterator's internal segment iterator positions.
  ///
  /// If the newly inserted or deleted key is after the internal segment iterator position,
  /// the new data is included in the iteration.
  ///
  /// Iterators are lightweight.
  /// Create them when you need and dispose them when you dont need.
  /// Iterators acquire locks on the disk segment and prevents its disposal.
  ///
  /// Use snapshot iterators for consistent view by ignoring new writes.
  /// </remarks>
  ///
  /// <param name="iteratorType">Defines iterator type.</param>
  /// <param name="includeDeletedRecords">if true the iterator retrieves
  /// the deleted and normal records.</param>
  /// <param name="contributeToTheBlockCache">If true, the iterator's disk segment reads contribute to the block cache,
  /// so later reads of the same blocks are served from memory. Set it to false for a one-pass scan over much of the
  /// data, such as an export or a full count, so that the scan does not fill the cache.</param>
  ///
  /// <returns>ZoneTree Iterator</returns>
  IZoneTreeIterator<TKey, TValue> CreateIterator(
      IteratorType iteratorType = IteratorType.AutoRefresh,
      bool includeDeletedRecords = false,
      bool contributeToTheBlockCache = true);

  /// <summary>
  /// Creates an iterator that enables scanning of the entire database.
  /// </summary>
  /// <param name="iteratorType">Defines iterator type.</param>
  /// <param name="includeDeletedRecords">if true the iterator retrieves
  /// the deleted and normal records.</param>
  /// <param name="contributeToTheBlockCache">If true, the iterator's disk segment reads contribute to the block cache,
  /// so later reads of the same blocks are served from memory. Set it to false for a one-pass scan over much of the
  /// data, such as an export or a full count, so that the scan does not fill the cache.</param>
  /// <param name="diskSegmentPrefetchSize">The number of sequential disk-segment
  /// records the iterator may prefetch at once. Values less than two disable
  /// prefetching.</param>
  /// <returns>ZoneTree Iterator</returns>
  IZoneTreeIterator<TKey, TValue> CreateIterator(
      IteratorType iteratorType,
      bool includeDeletedRecords,
      bool contributeToTheBlockCache,
      int diskSegmentPrefetchSize);

  /// <summary>
  /// Creates an iterator that enables scanning of the entire database.
  /// </summary>
  /// <param name="iteratorOptions">Runtime iterator options.</param>
  /// <returns>ZoneTree Iterator</returns>
  IZoneTreeIterator<TKey, TValue> CreateIterator(
      IteratorOptions iteratorOptions);

  /// <summary>
  /// Creates a reverse iterator that enables scanning of the entire database.
  /// </summary>
  ///
  /// <remarks>
  /// ZoneTree iterator direction does not hurt performance.
  /// Forward and backward iterator's performances are equal.
  /// </remarks>
  ///
  /// <param name="iteratorType">Defines iterator type.</param>
  /// <param name="includeDeletedRecords">if true the iterator retrieves
  /// the deleted and normal records.</param>
  /// <param name="contributeToTheBlockCache">If true, the iterator's disk segment reads contribute to the block cache,
  /// so later reads of the same blocks are served from memory. Set it to false for a one-pass scan over much of the
  /// data, such as an export or a full count, so that the scan does not fill the cache.</param>
  ///
  /// <returns>ZoneTree Iterator</returns>
  IZoneTreeIterator<TKey, TValue> CreateReverseIterator(
      IteratorType iteratorType = IteratorType.AutoRefresh,
      bool includeDeletedRecords = false,
      bool contributeToTheBlockCache = true);

  /// <summary>
  /// Creates a reverse iterator that enables scanning of the entire database.
  /// </summary>
  /// <param name="iteratorType">Defines iterator type.</param>
  /// <param name="includeDeletedRecords">if true the iterator retrieves
  /// the deleted and normal records.</param>
  /// <param name="contributeToTheBlockCache">If true, the iterator's disk segment reads contribute to the block cache,
  /// so later reads of the same blocks are served from memory. Set it to false for a one-pass scan over much of the
  /// data, such as an export or a full count, so that the scan does not fill the cache.</param>
  /// <param name="diskSegmentPrefetchSize">The number of sequential disk-segment
  /// records the iterator may prefetch at once. Values less than two disable
  /// prefetching.</param>
  /// <returns>ZoneTree Iterator</returns>
  IZoneTreeIterator<TKey, TValue> CreateReverseIterator(
      IteratorType iteratorType,
      bool includeDeletedRecords,
      bool contributeToTheBlockCache,
      int diskSegmentPrefetchSize);

  /// <summary>
  /// Creates a reverse iterator that enables scanning of the entire database.
  /// </summary>
  /// <param name="iteratorOptions">Runtime iterator options.</param>
  /// <returns>ZoneTree Iterator</returns>
  IZoneTreeIterator<TKey, TValue> CreateReverseIterator(
      IteratorOptions iteratorOptions);

  /// <summary>
  /// Syncs the write ahead log records of the writes that completed
  /// before this call to the disk, so that they survive a power loss.
  /// </summary>
  /// <remarks>
  /// Writes do not wait for disk syncs. Call this method when a write must
  /// be durable before you acknowledge it. Concurrent calls share disk syncs,
  /// so waiting for many writes costs about one disk sync.
  ///
  /// In AsyncPrivate mode the background writer syncs, and the calling
  /// thread does not block. In Private mode the calling
  /// thread syncs. It can append to its active tail during the sync;
  /// a rollover waits before writing to the main file.
  ///
  /// After a write ahead log write or disk sync fails, later calls fail too,
  /// because the operating system may have dropped records that were not
  /// synced yet.
  /// </remarks>
  /// <param name="cancellationToken">
  /// Cancels the wait. It does not cancel the disk sync.
  /// </param>
  /// <returns>
  /// A task that completes when the records are durable.
  /// It faults with <see cref="Exceptions.WriteAheadLogSyncFailedException"/>
  /// when they cannot be made durable. In
  /// <see cref="Options.WriteAheadLogMode.None"/> mode there is no write
  /// ahead log, and the task completes at once without making writes durable.
  /// </returns>
  Task SyncWalAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Returns maintenance object belongs to this ZoneTree.
  /// </summary>
  IZoneTreeMaintenance<TKey, TValue> Maintenance { get; }

  /// <summary>
  /// Enables read-only mode.
  /// </summary>
  bool IsReadOnly { get; set; }

  /// <summary>
  /// ZoneTree Logger.
  /// </summary>
  ILogger Logger { get; }

  /// <summary>
  /// Creates the default ZoneTree Maintainer.
  /// </summary>
  IMaintainer CreateMaintainer();
}

/// <summary>
/// Value updater delegate.
/// </summary>
/// <typeparam name="TValue">The value type</typeparam>
/// <param name="value">The value as a reference to be updated.</param>
/// <returns>true if the value is updated, false otherwise.</returns>
public delegate bool ValueUpdaterDelegate<TValue>(ref TValue value);
