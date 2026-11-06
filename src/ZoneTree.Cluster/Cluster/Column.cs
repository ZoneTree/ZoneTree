using System.Collections.Concurrent;
using ZoneTree.Logger;
using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

internal abstract class Column : IDisposable
{
  internal readonly ZoneTreeCluster Cluster;
  /// <summary>The catalog id of the column, which never changes and is never reused.</summary>
  internal readonly long Id;
  // Held by every domain commit that uses the column, taken in column id order. A domain
  // commit validates all of its views and prepares the views that wrote under these gates,
  // so no other commit to its columns comes between the validation and the commit records.
  internal readonly object CommitGate = new();

  readonly ColumnOperationGate Operations = new();
  readonly ConcurrentDictionary<IColumnDependent, byte> Dependents = new();
  string name;
  volatile bool dropped;

  /// <summary>The current name of the column. A rename changes it; the handle stays.</summary>
  public string Name => Volatile.Read(ref name);

  /// <summary>The domain of a coordinated column; null for other kinds.</summary>
  internal ClusterDomain Domain;

  /// <summary>True once the column was dropped.</summary>
  internal bool IsDropped => dropped;

  protected Column(ZoneTreeCluster cluster, long id, string name)
  {
    Cluster = cluster;
    Id = id;
    this.name = name;
  }

  internal void Rename(string newName) => Volatile.Write(ref name, newName);

  internal void ThrowIfDropped()
  {
    if (dropped)
      throw new SchemaObjectDroppedException($"Column '{Name}' was dropped.");
  }

  /// <summary>
  /// Starts creating a view, transaction or iterator that registers with the column.
  /// Throws once the column is dropped or closed.
  /// </summary>
  internal void BeginOperation()
  {
    if (Operations.TryBegin())
      return;
    ThrowIfDropped();
    ObjectDisposedException.ThrowIf(true, this);
  }

  internal void EndOperation() => Operations.End();

  /// <summary>Rejects new creations and waits for the running ones.</summary>
  protected void StopOperations() => Operations.Stop();

  internal void AddDependent(IColumnDependent dependent) => Dependents.TryAdd(dependent, 0);

  internal void RemoveDependent(IColumnDependent dependent) => Dependents.TryRemove(dependent, out _);

  /// <summary>Marks the column dropped, so its handles reject new uses at once.</summary>
  internal void MarkDropped() => dropped = true;

  /// <summary>
  /// Deletes a dropped column's storage: waits for running creations, ends its views,
  /// transactions and iterators, then drops the storage.
  /// </summary>
  internal void DropStorage(ILogger logger)
  {
    dropped = true;
    StopOperations();
    // A commit that holds the gate finishes first; the others find their views ended.
    lock (CommitGate)
    {
      foreach (var dependent in Dependents.Keys)
      {
        try { dependent.OnColumnDropped(); }
        catch (Exception error) { logger.LogError(error); }
      }
      Dependents.Clear();
    }
    DropTree();
  }

  internal abstract ColumnView CreateView(bool writing, TransactionIsolationLevel isolation,
      long transactionId, long snapshotTimestamp);
  internal abstract Task SyncWALAsync();
  internal abstract TimeSpan AutoSyncInterval { get; set; }
  internal abstract int AbortedTransactionCount { get; }
  internal abstract int ConflictIndexCount { get; }

  /// <summary>
  /// The active transactions of a column that counts its own: a standalone column. A
  /// domain counts the transactions of its coordinated columns.
  /// </summary>
  internal virtual int ActiveTransactionCount => 0;
  internal virtual void SetReadOnly(bool value) { }
  internal abstract void SaveMetaData();

  /// <summary>
  /// Creates the column's part of a cluster backup generation, with the store in the options.
  /// Called under the schema lock, while the domains' cuts are pinned: a coordinated column
  /// captures its storage here, at a point that includes its domain's cut.
  /// </summary>
  internal abstract ColumnBackup CreateBackup(Backup.LiveBackupOptions options);

  /// <summary>Tells the cluster's live backups that a merge of the column's storage succeeded.</summary>
  protected void OnStorageMergeEnded(MergeResult result)
  {
    if (result == MergeResult.SUCCESS)
      Cluster.OnColumnMergeSucceeded();
  }

  /// <summary>Closes and deletes the storage. Called after the operations stopped.</summary>
  protected abstract void DropTree();

  public abstract IMaintainer CreateMaintainer();
  public abstract void Dispose();
}
