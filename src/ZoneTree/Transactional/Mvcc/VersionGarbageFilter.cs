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
    }
    switch (Tree.GetVersionState(
        key.Version,
        Horizon,
        DurableCommitTimestamp,
        MergedSegmentId,
        out var commitTimestamp))
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
    if (IsCurrentKeyHidden)
      return false;
    IsCurrentKeyHidden = true;
    // Every snapshot sees this version, unless a newer one hides it from all of them.
    if (Horizon >= NewerCommitTimestamp)
      return false;
    return !(IncludesOldestRecords && value.Kind == VersionedValueKind.Tombstone);
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
    return !(HasCurrentKey &&
        firstKey.Kind == VersionedKeyKind.Data &&
        Comparer.Compare(in CurrentKey, in firstKey.Key) == 0);
  }

  public void OnPartSkipped(
      in VersionedKey<TKey> lastKey, in VersionedValue<TValue> lastValue)
  {
    // The next part may continue the last key of the copied part. The copied
    // part ends with the oldest version of that key so far, and a committed
    // or visible version decides alone what it hides of the older ones.
    // Otherwise the rest is treated as a new key, which keeps it unless a
    // version visible to all hides it.
    HasCurrentKey = false;
    if (lastKey.Kind != VersionedKeyKind.Data ||
        lastValue.Kind == VersionedValueKind.Deleted)
      return;
    switch (Tree.GetVersionState(
        lastKey.Version,
        Horizon,
        DurableCommitTimestamp,
        MergedSegmentId,
        out var commitTimestamp))
    {
      case VersionState.Committed:
        NewerCommitTimestamp = commitTimestamp;
        IsCurrentKeyHidden = false;
        break;
      case VersionState.VisibleToAll:
        IsCurrentKeyHidden = true;
        break;
      default:
        return;
    }
    CurrentKey = lastKey.Key;
    HasCurrentKey = true;
  }
}
