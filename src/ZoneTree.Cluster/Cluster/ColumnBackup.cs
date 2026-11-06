using ZoneTree.Backup;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.Cluster;

/// <summary>
/// One column's part of a cluster backup generation: an engine live backup of the column's
/// storage into the column's own store.
/// </summary>
internal abstract class ColumnBackup : IDisposable
{
  internal long ColumnId { get; }

  protected ColumnBackup(long columnId) => ColumnId = columnId;

  /// <summary>Backs the column up and returns the generation id its store gave the backup.</summary>
  internal abstract Task<long> RunAsync(CancellationToken cancellationToken);

  public abstract void Dispose();
}

/// <summary>The backup of a tree captured when the generation began.</summary>
internal sealed class CapturedColumnBackup<TKey, TValue> : ColumnBackup
{
  readonly global::ZoneTree.Core.ZoneTree<TKey, TValue> Tree;
  readonly LiveBackupOptions Options;

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
      Justification = "Dispose, or the live backup that takes it, disposes the capture.")]
  global::ZoneTree.Core.ZoneTree<TKey, TValue>.BackupSegmentCollection Captured;

  internal CapturedColumnBackup(long columnId, global::ZoneTree.Core.ZoneTree<TKey, TValue> tree,
      LiveBackupOptions options, global::ZoneTree.Core.ZoneTree<TKey, TValue>.BackupSegmentCollection captured)
      : base(columnId)
  {
    Tree = tree;
    Options = options;
    Captured = captured;
  }

  internal override async Task<long> RunAsync(CancellationToken cancellationToken)
  {
    using var backup = new LiveBackup<TKey, TValue>(Tree, Options, TakeCapture);
    await backup.CreateGenerationAsync(cancellationToken).ConfigureAwait(false);
    return backup.CurrentGenerationId;
  }

  /// <summary>The live backup disposes the capture once it copied it.</summary>
  global::ZoneTree.Core.ZoneTree<TKey, TValue>.BackupSegmentCollection TakeCapture()
  {
    var captured = Interlocked.Exchange(ref Captured, null);
    return captured ?? throw new InvalidOperationException("The column's capture was already used.");
  }

  public override void Dispose() => Interlocked.Exchange(ref Captured, null)?.Dispose();
}

/// <summary>The backup of a tree that captures itself when the backup runs.</summary>
internal sealed class LiveColumnBackup(long columnId, Func<ILiveBackup> create) : ColumnBackup(columnId)
{
  internal override async Task<long> RunAsync(CancellationToken cancellationToken)
  {
    using var backup = create();
    await backup.CreateGenerationAsync(cancellationToken).ConfigureAwait(false);
    return backup.CurrentGenerationId;
  }

  public override void Dispose() { }
}

/// <summary>
/// The in-memory records of a coordinated column for a backup at a domain cut, iterated while
/// writers go on, without the versions that could lack their status record.
/// </summary>
/// <remarks>
/// <para>
/// Status records sort before the versions, and writers go on while the iterator runs, so the
/// capture can hold a version without the commit record that follows it. A restore rolls such
/// a transaction back, as recovery rolls back every restored version without a status record,
/// but the capture leaves out the versions it can tell apart. Only a transaction that still
/// wrote at the cut, or began later, can write them: its id is at least the oldest writer's
/// id at the cut.
/// </para>
/// <para>
/// A committed version lacks its status record only once the record was dropped, which waits
/// until every transaction that began before the commit has ended, so its transaction's id is
/// below that oldest writer's id. A version at or above it is kept only if the capture holds
/// its transaction's commit record, which the iterator met first: the captured segments do not
/// change, and a merge does not drop such a record while that writer runs. Every status record
/// and every deletion of a version is kept; restore rolls back the commits after the cut.
/// </para>
/// </remarks>
internal sealed class CutIterator<TKey, TValue>(
    IZoneTreeIterator<VersionedKey<TKey>, VersionedValue<TValue>> inner, long oldestWriterId)
    : IZoneTreeIterator<VersionedKey<TKey>, VersionedValue<TValue>>
{
  /// <summary>The transactions at or above the oldest writer's id whose commit record was met.</summary>
  readonly HashSet<long> Committed = [];

  public VersionedKey<TKey> CurrentKey => inner.CurrentKey;

  public VersionedValue<TValue> CurrentValue => inner.CurrentValue;

  public KeyValuePair<VersionedKey<TKey>, VersionedValue<TValue>> Current => inner.Current;

  public bool HasCurrent => inner.HasCurrent;

  public bool AutoRefresh => inner.AutoRefresh;

  public bool Next()
  {
    while (inner.Next())
    {
      var key = inner.CurrentKey;
      if (key.Kind != VersionedKeyKind.Data)
      {
        if (key.Kind == VersionedKeyKind.TransactionStatus && key.Version >= oldestWriterId)
        {
          var value = inner.CurrentValue;
          if (value.Kind == VersionedValueKind.System && value.Meta != CommitRecord.Pending)
            Committed.Add(key.Version);
        }
        return true;
      }
      // A rollback's deletion stays: its version may be on the disk, which the capture copies.
      if (key.Version < oldestWriterId || Committed.Contains(key.Version) ||
          inner.CurrentValue.Kind == VersionedValueKind.Deleted)
        return true;
    }
    return false;
  }

  public void Seek(in VersionedKey<TKey> key) => inner.Seek(in key);

  public void SeekFirst() => inner.SeekFirst();

  public void Refresh() => inner.Refresh();

  public void Dispose() => inner.Dispose();
}
