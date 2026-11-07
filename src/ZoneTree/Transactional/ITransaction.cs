namespace ZoneTree.Transactional;

/// <summary>
/// A transaction that reads and writes a transactional ZoneTree.
/// </summary>
/// <remarks>
/// A transaction reads the snapshot taken when it began, plus its own writes.
/// Its methods can be called concurrently, but each iterator is used by one caller at a
/// time. A commit or rollback waits for the running operations, and the first of them
/// ends the transaction. Disposing an active transaction rolls it back.
/// Conflicts are reported by the commit, not by exceptions from reads or writes.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface ITransaction<TKey, TValue> : IReadTransaction<TKey, TValue>
{
  /// <summary>
  /// The transaction id.
  /// </summary>
  long TransactionId { get; }

  /// <summary>
  /// The commit timestamp, or 0 until the transaction commits.
  /// A transaction that wrote nothing commits at its snapshot timestamp.
  /// </summary>
  long CommitTimestamp { get; }

  /// <summary>
  /// The isolation level.
  /// </summary>
  TransactionIsolationLevel IsolationLevel { get; }

  /// <summary>
  /// The state of the transaction.
  /// </summary>
  TransactionState State { get; }

  /// <summary>
  /// True once a write found its key committed or reserved by a prepared commit
  /// after this transaction's snapshot.
  /// The transaction stays active, but stores no more writes and its commit fails.
  /// </summary>
  /// <remarks>
  /// Writes report conflicts here instead of throwing. A long transaction can check it
  /// to stop early; the commit reports the conflict anyway.
  /// </remarks>
  bool HasConflict { get; }

  /// <summary>
  /// Gets the value of the key and protects it like a write: at every isolation level,
  /// the commit of a transaction that writes fails if another transaction committed
  /// or prepared a write to the key after this transaction's snapshot.
  /// </summary>
  /// <remarks>
  /// Use it in a snapshot transaction for the keys a decision depends on, to prevent
  /// write skew without tracking every read. A transaction that writes nothing
  /// commits at its snapshot, so its reads need no protection.
  /// </remarks>
  bool TryGetForUpdate(in TKey key, out TValue value);

  /// <summary>
  /// Writes the value of the key. If a transaction committed or prepared a write to
  /// the key after this one's snapshot, the write is not stored and <see cref="HasConflict"/> becomes true.
  /// </summary>
  void Upsert(in TKey key, in TValue value);

  /// <summary>
  /// Deletes the key. If a transaction committed or prepared a write to the key after
  /// this one's snapshot, the delete is not stored and <see cref="HasConflict"/> becomes true.
  /// </summary>
  void Delete(in TKey key);

  /// <summary>
  /// Commits the transaction. The commit becomes durable with the next write ahead log sync.
  /// </summary>
  /// <remarks>
  /// The commit is visible when this returns. While a durable commit waits for its sync,
  /// later commits wait for its publication too, so readers see commits in commit order.
  /// If that sync fails, this call throws the failure and the transaction stays
  /// <see cref="TransactionState.InDoubt"/>: recovery may find its commit record.
  /// </remarks>
  /// <exception cref="Exceptions.TransactionConflictException">
  /// The transaction conflicts with a transaction that committed or prepared a commit after its snapshot.
  /// The transaction is rolled back.</exception>
  /// <exception cref="Exceptions.TransactionalTreeCommitsStoppedException">
  /// A previous commit sync of the tree failed. The transaction is rolled back.</exception>
  /// <exception cref="Exceptions.WriteAheadLogSyncFailedException">
  /// The sync of a durable commit this commit waited for failed. The transaction is in doubt.</exception>
  void Commit();

  /// <summary>
  /// Commits the transaction unless it conflicts.
  /// </summary>
  /// <remarks>
  /// Waits like <see cref="Commit"/> when a durable commit is waiting for its sync.
  /// </remarks>
  /// <returns>true if committed; false if the transaction conflicted and was rolled back.</returns>
  /// <exception cref="Exceptions.TransactionalTreeCommitsStoppedException">
  /// A previous commit sync of the tree failed. The transaction is rolled back.</exception>
  /// <exception cref="Exceptions.WriteAheadLogSyncFailedException">
  /// The sync of a durable commit this commit waited for failed. The transaction is in doubt.</exception>
  bool TryCommit();

  /// <summary>
  /// Syncs the commit record before making the transaction visible to readers.
  /// Concurrent callers share disk syncs. A writing transaction whose sync fails
  /// stays InDoubt and invisible in this instance; recovery may find its commit record.
  /// </summary>
  /// <param name="cancellationToken">Cancels before starting, or stops waiting after the
  /// commit record is appended. A started commit continues and publishes after sync.</param>
  /// <exception cref="Exceptions.TransactionConflictException">
  /// The transaction conflicts with a transaction that committed or prepared a commit after its snapshot.
  /// The transaction is rolled back.</exception>
  /// <exception cref="Exceptions.TransactionalTreeCommitsStoppedException">
  /// A previous commit sync of the tree failed. The transaction is rolled back.</exception>
  Task CommitDurableAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Commits the transaction unless it conflicts, syncing the commit record before
  /// making it visible to readers. Concurrent callers share disk syncs. A writing
  /// transaction whose sync fails stays InDoubt; recovery may find its commit record.
  /// </summary>
  /// <param name="cancellationToken">Cancels before starting, or stops waiting after the
  /// commit record is appended. A started commit continues and publishes after sync.</param>
  /// <returns>true if committed; false if the transaction conflicted and was rolled back.</returns>
  /// <exception cref="Exceptions.TransactionalTreeCommitsStoppedException">
  /// A previous commit sync of the tree failed. The transaction is rolled back.</exception>
  Task<bool> TryCommitDurableAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Rolls back the transaction. Does nothing if the transaction is not active.
  /// </summary>
  void Rollback();
}
