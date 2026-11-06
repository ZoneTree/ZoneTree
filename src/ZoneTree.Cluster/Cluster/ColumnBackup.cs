using ZoneTree.Backup;

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
      LiveBackupOptions options) : base(columnId)
  {
    Tree = tree;
    Options = options;
    // The in-memory segments freeze, so the capture is the tree at this point.
    Captured = tree.CollectBackupSegments(moveMutableSegmentForward: true, includeInMemorySegments: true);
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
