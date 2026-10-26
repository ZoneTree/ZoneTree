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
  /// Tries to add the specified key and value to the collection.
  /// </summary>
  /// <param name="key">The key of the element to add.</param>
  /// <param name="value">The value of the element to add.</param>
  /// <param name="opIndex">The operation index assigned to the successful write.</param>
  /// <returns>true if the key and value were added successfully; otherwise, false if the key already exists.</returns>
  bool TryAdd(in TKey key, in TValue value, out long opIndex);

  /// <summary>
  /// Tries to get the value of the given key and
  /// updates the value using value updater if found any.
  /// </summary>
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
  /// Tries to get the value of the given key and
  /// updates the value atomically using value updater if found any.
  /// </summary>
  /// <remarks>
  /// Atomic methods are synchronized with other atomic methods across LSM-tree segments.
  /// Use them when the new value depends on the current value. For simple inserts
  /// or replacements, prefer <see cref="Upsert(in TKey, in TValue)"/> because it is faster.
  /// </remarks>
  /// <param name="key">The key of the element.</param>
  /// <param name="value">The value of the element associated with the key.</param>
  /// <param name="valueUpdater">The delegate function that updates the value.</param>
  /// <returns>true if the key is found; otherwise, false</returns>
  bool TryAtomicGetAndUpdate(
      in TKey key,
      out TValue value,
      ValueUpdaterDelegate<TValue> valueUpdater);

  /// <summary>
  /// Attempts to add the specified key and value atomically across LSM-Tree segments.
  /// </summary>
  /// <param name="key">The key of the element to add.</param>
  /// <param name="value">The value of the element to add. It can be null.</param>
  /// <param name="opIndex">The operation index assigned to the successful write.</param>
  /// <returns>true if the key/value pair was added successfully;
  /// otherwise, false.</returns>
  bool TryAtomicAdd(in TKey key, in TValue value, out long opIndex);

  /// <summary>
  /// Attempts to update the specified key's value atomically across LSM-Tree segments.
  /// </summary>
  /// <param name="key">The key of the element to update.</param>
  /// <param name="value">The value of the element to update. It can be null.</param>
  /// <param name="opIndex">The operation index assigned to the successful update.</param>
  /// <returns>true if the key/value pair was updated successfully;
  /// otherwise, false.</returns>
  bool TryAtomicUpdate(in TKey key, in TValue value, out long opIndex);

  /// <summary>
  /// Attempts to add or update the specified key and value atomically across LSM-Tree segments  and calls the result delegate atomically.
  /// valueUpdater can be called one or more times. Keep it deterministic and avoid side effects that cannot be repeated.
  /// When TValue is a mutable reference type, assigning a new object is the safest pattern.
  /// In-place mutation should be idempotent and must not be followed by cancellation.
  /// </summary>
  /// <param name="key">The key of the element to add.</param>
  /// <param name="valueToAdd">The value of the element to add. It can be null.</param>
  /// <param name="valueUpdater">The delegate function that updates the value.</param>
  /// <param name="result">The operation result delegate.</param>
  /// <returns>true if the key/value pair was added;
  /// false, if the key/value pair was updated.</returns>
  bool TryAtomicAddOrUpdate(
      in TKey key,
      in TValue valueToAdd,
      ValueUpdaterDelegate<TValue> valueUpdater,
      OperationResultDelegate<TValue> result = null);

  /// <summary>
  /// Attempts to add or update the specified key and value atomically across LSM-Tree segments and calls the result delegate atomically.
  /// valueAdder can be called one or more times.
  /// valueUpdater can be called one or more times. Keep delegates deterministic and avoid side effects that cannot be repeated.
  /// When TValue is a mutable reference type, assigning a new object is the safest pattern.
  /// In-place mutation should be idempotent and must not be followed by cancellation.
  /// </summary>
  /// <param name="key">The key of the element to add.</param>
  /// <param name="valueAdder">The delegate function that adds the value.</param>
  /// <param name="valueUpdater">The delegate function that updates the value.</param>
  /// <param name="result">The operation result delegate.</param>
  /// <returns>true if the key/value pair was added;
  /// false, if the key/value pair was updated.</returns>
  bool TryAtomicAddOrUpdate(
      in TKey key,
      ValueAdderDelegate<TValue> valueAdder,
      ValueUpdaterDelegate<TValue> valueUpdater,
      OperationResultDelegate<TValue> result = null);

  /// <summary>
  /// Adds or updates the specified key/value pair atomically across LSM-Tree segments.
  /// </summary>
  /// <param name="key">The key of the element to upsert.</param>
  /// <param name="value">The value of the element to upsert.</param>
  /// <returns>The operation index assigned to the write.</returns>
  long AtomicUpsert(in TKey key, in TValue value);

  /// <summary>
  /// Adds or updates the specified key/value pair.
  /// </summary>
  /// <remarks>
  /// This is a thread-safe method, but it is not synchronized
  /// with other atomic add/update/upsert methods.
  /// Using the Upsert method in parallel to atomic methods breaks the atomicity
  /// of the atomic methods.
  ///
  /// For example:
  /// TryAtomicAddOrUpdate(key) does the following 3 things
  /// within lock to preserve atomicity across segments of LSM-Tree.
  ///  1. tries to get the value of the key
  ///  2. if it can find the key, it updates the value
  ///  3. if it cannot find the key, inserts the new key/value
  ///
  /// All atomic methods respect this order using the same lock.
  ///
  /// The Upsert method does not respect to the atomicity of atomic methods,
  /// because it does upsert without lock.
  ///
  /// On the other hand,
  /// the Upsert method is atomic in the mutable segment scope but not across all segments.
  /// This makes Upsert method thread-safe.
  ///
  /// This is the fastest add or update function.
  /// </remarks>
  /// <param name="key">The key of the element to upsert.</param>
  /// <param name="value">The value of the element to upsert.</param>
  /// <returns>The operation index. It can be used to distrubute the operations in stable order.</returns>
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
  /// <param name="contributeToTheBlockCache">if true the iterator disk segment reads
  /// contributes to the block cache.</param>
  ///
  /// <returns>ZoneTree Iterator</returns>
  IZoneTreeIterator<TKey, TValue> CreateIterator(
      IteratorType iteratorType = IteratorType.AutoRefresh,
      bool includeDeletedRecords = false,
      bool contributeToTheBlockCache = false);

  /// <summary>
  /// Creates an iterator that enables scanning of the entire database.
  /// </summary>
  /// <param name="iteratorType">Defines iterator type.</param>
  /// <param name="includeDeletedRecords">if true the iterator retrieves
  /// the deleted and normal records.</param>
  /// <param name="contributeToTheBlockCache">if true the iterator disk segment reads
  /// contributes to the block cache.</param>
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
  /// <param name="contributeToTheBlockCache">if true the iterator disk segment reads
  /// contributes to the block cache</param>
  ///
  /// <returns>ZoneTree Iterator</returns>
  IZoneTreeIterator<TKey, TValue> CreateReverseIterator(
      IteratorType iteratorType = IteratorType.AutoRefresh,
      bool includeDeletedRecords = false,
      bool contributeToTheBlockCache = false);

  /// <summary>
  /// Creates a reverse iterator that enables scanning of the entire database.
  /// </summary>
  /// <param name="iteratorType">Defines iterator type.</param>
  /// <param name="includeDeletedRecords">if true the iterator retrieves
  /// the deleted and normal records.</param>
  /// <param name="contributeToTheBlockCache">if true the iterator disk segment reads
  /// contributes to the block cache.</param>
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
  /// In AsyncCompressed mode the background writer syncs, and the calling
  /// thread does not block. In Sync and SyncCompressed modes the calling
  /// thread syncs. In Sync mode appends to the write ahead log wait for
  /// the sync.
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
  Task SyncWALAsync(CancellationToken cancellationToken = default);

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

/// <summary>
/// Value adder delegate.
/// </summary>
/// <typeparam name="TValue">The value type</typeparam>
/// <param name="value">The value as a reference to be added.</param>
/// <returns>true if the value is added, false otherwise.</returns>
public delegate bool ValueAdderDelegate<TValue>(ref TValue value);

public enum OperationResult
{
  Added,
  Updated,
  Cancelled
}

/// <summary>
/// Value adder delegate.
/// </summary>
/// <typeparam name="TValue">The value type</typeparam>
/// <param name="value">The value that has been updated or added.</param>
/// <param name="opIndex">The operation index assigned to the write.</param>
/// <param name="operationResult">The operation result.</param>
public delegate void OperationResultDelegate<TValue>(in TValue value, long opIndex, OperationResult operationResult);
