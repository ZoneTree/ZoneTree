using ZoneTree.Transactional.Mvcc;

namespace ZoneTree.Backup;

public static class LiveBackupExtensions
{
  /// <summary>Creates a consistent transactional live backup with incremental disk-file reuse.</summary>
  public static ILiveBackup CreateLiveBackup<TKey, TValue>(this ITransactionalZoneTree<TKey, TValue> tree, string directory)
      => tree.CreateLiveBackup(new LiveBackupOptions { Store = new LocalLiveBackupProvider(directory) });

  public static ILiveBackup CreateLiveBackup<TKey, TValue>(this ITransactionalZoneTree<TKey, TValue> tree, LocalLiveBackupOptions options)
      => tree.CreateLiveBackup(new LiveBackupOptions { Store = new LocalLiveBackupProvider(options) });

  public static ILiveBackup CreateLiveBackup<TKey, TValue>(this ITransactionalZoneTree<TKey, TValue> tree, ILiveBackupStore store)
      => tree.CreateLiveBackup(new LiveBackupOptions { Store = store });

  public static ILiveBackup CreateLiveBackup<TKey, TValue>(this ITransactionalZoneTree<TKey, TValue> tree, LiveBackupOptions options)
  {
    ArgumentNullException.ThrowIfNull(tree);
    if (tree is not MvccZoneTree<TKey, TValue> mvcc)
      throw new NotSupportedException("Transactional live backup requires the built-in MVCC tree.");
    return mvcc.CreateLiveBackup(options);
  }

  /// <summary>
  /// Creates a live backup coordinator that writes into a local directory.
  /// </summary>
  public static LiveBackup<TKey, TValue> CreateLiveBackup<TKey, TValue>(
      this IZoneTree<TKey, TValue> zoneTree,
      string directory)
  {
    ArgumentNullException.ThrowIfNull(zoneTree);

    return new LiveBackup<TKey, TValue>(
        zoneTree,
        new LiveBackupOptions
        {
          Store = new LocalLiveBackupProvider(directory)
        });
  }

  /// <summary>
  /// Creates a live backup coordinator that writes into a local directory.
  /// </summary>
  public static LiveBackup<TKey, TValue> CreateLiveBackup<TKey, TValue>(
      this IZoneTree<TKey, TValue> zoneTree,
      LocalLiveBackupOptions localOptions)
  {
    ArgumentNullException.ThrowIfNull(zoneTree);

    return new LiveBackup<TKey, TValue>(
        zoneTree,
        new LiveBackupOptions
        {
          Store = new LocalLiveBackupProvider(localOptions)
        });
  }

  /// <summary>
  /// Creates a live backup coordinator that writes through the specified store.
  /// </summary>
  public static LiveBackup<TKey, TValue> CreateLiveBackup<TKey, TValue>(
      this IZoneTree<TKey, TValue> zoneTree,
      ILiveBackupStore store)
  {
    ArgumentNullException.ThrowIfNull(zoneTree);

    return new LiveBackup<TKey, TValue>(
        zoneTree,
        new LiveBackupOptions { Store = store });
  }

  /// <summary>
  /// Creates a live backup coordinator for this ZoneTree.
  /// </summary>
  public static LiveBackup<TKey, TValue> CreateLiveBackup<TKey, TValue>(
      this IZoneTree<TKey, TValue> zoneTree,
      LiveBackupOptions options)
  {
    ArgumentNullException.ThrowIfNull(zoneTree);

    return new LiveBackup<TKey, TValue>(zoneTree, options);
  }

}
