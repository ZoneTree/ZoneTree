using ZoneTree.Comparers;
using ZoneTree.Options;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.Transactional.Mvcc;

/// <summary>
/// Drops the versions no snapshot can read while the storage tree merges.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot reads the newest version committed at or before its timestamp. The versions
/// of a key arrive newest first, which for committed versions is also commit order, so a
/// version is needed only if some snapshot falls between its commit and the next newer
/// commit. The filter knows the snapshots active when the merge began and the latest commit,
/// which stands for every snapshot that begins later.
/// </para>
/// <para>
/// The newest version visible to every snapshot hides all older versions. If it is a delete
/// and no older record remains outside the merge, it is dropped too: nothing older can reappear.
/// Versions of active transactions are kept, and versions of rolled back transactions dropped.
/// </para>
/// <para>
/// A commit hides older versions only once it is durable: synced, or written to the disk
/// by this merge or an earlier one. Otherwise a crash could keep the merge output but
/// lose the commit, and recovery would find neither the new value nor the old one.
/// </para>
/// <para>
/// A version without an entry is committed unless the storage tree deleted it for a
/// rollback the tree forgot, and its deletion can be in a segment outside the merge.
/// So before such a version hides an older one, the filter looks up its newest stored
/// copy, and a deletion hides nothing. Keys with a single version never need the lookup.
/// </para>
/// </remarks>
internal sealed class VersionGarbageFilter<TKey, TValue> :
    IMergeFilter<VersionedKey<TKey>, VersionedValue<TValue>>
{
  readonly MvccZoneTree<TKey, TValue> Tree;

  readonly IRefComparer<TKey> Comparer;

  readonly bool IncludesOldestRecords;

  /// <summary>
  /// The active snapshot timestamps in ascending order, ending with the latest commit.
  /// </summary>
  readonly long[] Snapshots;

  readonly long Horizon;

  readonly long LatestSnapshot;

  readonly long DurableCommitTimestamp;

  readonly long MergedSegmentId;

  bool HasCurrentKey;

  TKey CurrentKey;

  /// <summary>
  /// The commit timestamp of the previous committed version of the current key.
  /// </summary>
  long NewerCommitTimestamp;

  bool IsCurrentKeyHidden;

  /// <summary>
  /// True while the version that hides the current key has no entry
  /// and its newest stored copy was not looked up.
  /// </summary>
  bool IsHidingUnconfirmed;

  VersionedKey<TKey> HidingVersion;

  public VersionGarbageFilter(
      MvccZoneTree<TKey, TValue> tree,
      IRefComparer<TKey> comparer,
      bool includesOldestRecords,
      long[] snapshots,
      long durableCommitTimestamp,
      long mergedSegmentId)
  {
    Tree = tree;
    Comparer = comparer;
    IncludesOldestRecords = includesOldestRecords;
    Snapshots = snapshots;
    Horizon = snapshots[0];
    LatestSnapshot = snapshots[^1];
    DurableCommitTimestamp = durableCommitTimestamp;
    MergedSegmentId = mergedSegmentId;
  }

  public bool Keep(in VersionedKey<TKey> key, in VersionedValue<TValue> value)
  {
    if (key.Kind != VersionedKeyKind.Data)
      return true;
    if (!HasCurrentKey || Comparer.Compare(in CurrentKey, in key.Key) != 0)
    {
      CurrentKey = key.Key;
      HasCurrentKey = true;
      NewerCommitTimestamp = long.MaxValue;
      IsCurrentKeyHidden = false;
      IsHidingUnconfirmed = false;
    }
    var state = Tree.GetVersionState(
        key.Version,
        Horizon,
        DurableCommitTimestamp,
        MergedSegmentId,
        out var commitTimestamp);
    switch (state)
    {
      case VersionState.Aborted:
        return false;
      case VersionState.Active:
        return true;
      case VersionState.Committed:
        {
          // Committed after the merge began, or read by a snapshot before the newer commit.
          var isNeeded = commitTimestamp > LatestSnapshot ||
              HasSnapshotBetween(commitTimestamp, NewerCommitTimestamp);
          NewerCommitTimestamp = commitTimestamp;
          return isNeeded;
        }
    }
    if (IsCurrentKeyHidden && IsHidingConfirmed())
      return false;
    Hide(in key, state);
    // Every snapshot sees this version, unless a newer one hides it from all of them.
    if (Horizon >= NewerCommitTimestamp)
      return false;
    return !(IncludesOldestRecords && value.Kind == VersionedValueKind.Tombstone);
  }

  /// <summary>
  /// Makes a version visible to every snapshot hide the older versions of the current key.
  /// </summary>
  void Hide(in VersionedKey<TKey> version, VersionState state)
  {
    IsCurrentKeyHidden = true;
    IsHidingUnconfirmed = state == VersionState.Unregistered;
    if (IsHidingUnconfirmed)
      HidingVersion = version;
  }

  /// <summary>
  /// Checks, before the first older version is dropped, that a hiding version without
  /// an entry was not deleted for a rollback. A deleted one hides nothing.
  /// </summary>
  bool IsHidingConfirmed()
  {
    if (!IsHidingUnconfirmed)
      return true;
    IsHidingUnconfirmed = false;
    if (!Tree.IsDeletedVersion(in HidingVersion))
      return true;
    IsCurrentKeyHidden = false;
    return false;
  }

  /// <summary>
  /// True if a snapshot is in [low, high).
  /// </summary>
  bool HasSnapshotBetween(long low, long high)
  {
    var index = Array.BinarySearch(Snapshots, low);
    if (index < 0)
      index = ~index;
    return index < Snapshots.Length && Snapshots[index] < high;
  }

  public bool CanSkipPart(in VersionedKey<TKey> firstKey)
  {
    // A copied part would keep the older versions of the current key
    // that this filter drops, including the ones under a dropped delete.
    // Any other part starts after the current key, so the next record is
    // a new key, even one whose newer versions are in the copied part.
    return !(HasCurrentKey &&
        firstKey.Kind == VersionedKeyKind.Data &&
        Comparer.Compare(in CurrentKey, in firstKey.Key) == 0);
  }
}
