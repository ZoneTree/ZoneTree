using ZoneTree.Comparers;
using ZoneTree.Exceptions;
using ZoneTree.Transactional.Conflicts;
using ZoneTree.Transactional.Versioning;

namespace ZoneTree.Transactional.Mvcc;

/// <summary>
/// Iterates the snapshot of a transaction over the versions in the storage tree.
/// </summary>
/// <remarks>
/// <para>
/// Each user key yields its newest version visible to the transaction, unless that
/// version is a delete. Forward iteration meets the versions of a key newest first,
/// reverse iteration meets them oldest first.
/// </para>
/// <para>
/// In a serializable transaction, the iterator records the range from its seek key
/// to the last key it read, or to the end once it is exhausted. A commit after the
/// snapshot that writes inside the range, even a new key, conflicts.
/// </para>
/// </remarks>
internal sealed class MvccTransactionIterator<TKey, TValue> : ITransactionIterator<TKey, TValue>
{
  readonly MvccTransaction<TKey, TValue> Transaction;

  readonly IRefComparer<TKey> Comparer;

  readonly IZoneTreeIterator<VersionedKey<TKey>, VersionedValue<TValue>> Inner;

  readonly bool IsReverse;

  bool IsPositioned;

  bool IsExhausted;

  // Forward iteration state.
  bool HasGroup;

  TKey GroupKey;

  bool IsGroupResolved;

  // Reverse iteration reads one record ahead to find the end of a key's versions.
  bool HasPending;

  VersionedKey<TKey> PendingKey;

  VersionedValue<TValue> PendingValue;

  KeyRange<TKey> Range;

  int RangeIndex = -1;

  bool isDisposed;

  TKey currentKey;

  TValue currentValue;

  public TKey CurrentKey =>
      HasCurrent ? currentKey : throw new ZoneTreeIteratorPositionException();

  public TValue CurrentValue =>
      HasCurrent ? currentValue : throw new ZoneTreeIteratorPositionException();

  public bool HasCurrent { get; private set; }

  public KeyValuePair<TKey, TValue> Current => KeyValuePair.Create(CurrentKey, CurrentValue);

  public MvccTransactionIterator(
      MvccTransaction<TKey, TValue> transaction,
      MvccZoneTree<TKey, TValue> tree,
      IRefComparer<TKey> comparer,
      bool isReverse,
      TransactionIteratorOptions options)
  {
    Transaction = transaction;
    Comparer = comparer;
    IsReverse = isReverse;
    Inner = tree.CreateStorageIterator(
        isReverse,
        options?.ContributeToTheBlockCache ?? false,
        options?.DiskSegmentPrefetchSize ?? 0);
  }

  public void Seek(in TKey key)
  {
    EnsureUsable();
    ResetPosition();
    // The newest version sorts first, so a forward seek starts at the newest
    // version of the key and a reverse seek at its oldest.
    Inner.Seek(VersionedKey<TKey>.Data(in key, IsReverse ? long.MinValue : long.MaxValue));
    if (IsReverse)
      StartRange(new KeyRange<TKey> { High = key, HasHigh = true, Low = key, HasLow = true });
    else
      StartRange(new KeyRange<TKey> { Low = key, HasLow = true, High = key, HasHigh = true });
  }

  public void SeekFirst()
  {
    EnsureUsable();
    ResetPosition();
    Inner.SeekFirst();
    // The range starts at the start of the key space (the end in reverse)
    // and grows as keys are read.
    StartRange(new KeyRange<TKey>());
  }

  public bool Next()
  {
    EnsureUsable();
    if (!IsPositioned)
      SeekFirst();
    var found = IsReverse ? NextReverse() : NextForward();
    if (!found)
    {
      HasCurrent = false;
      // An exhausted iterator has read up to the end of the key space.
      if (IsReverse)
        Range.HasLow = false;
      else
        Range.HasHigh = false;
    }
    // Every key examined during the call joins the read set,
    // including skipped versions after the last returned key.
    PublishRange();
    return found;
  }

  /// <summary>
  /// Reads the next stored record. An exhausted storage iterator would start
  /// over after a refresh, so it is not read again until the next seek.
  /// </summary>
  bool NextStored()
  {
    if (IsExhausted)
      return false;
    if (Inner.Next())
      return true;
    IsExhausted = true;
    return false;
  }

  bool NextForward()
  {
    while (NextStored())
    {
      var key = Inner.CurrentKey;
      // System records sort before the data records.
      if (key.Kind != VersionedKeyKind.Data)
        continue;
      Extend(in key.Key);
      if (HasGroup && Comparer.Compare(in GroupKey, in key.Key) == 0)
      {
        if (IsGroupResolved)
          continue;
      }
      else
      {
        GroupKey = key.Key;
        HasGroup = true;
        IsGroupResolved = false;
      }
      if (!Transaction.IsVisible(key.Version))
        continue;
      IsGroupResolved = true;
      var value = Inner.CurrentValue;
      if (value.Kind == VersionedValueKind.Tombstone)
        continue;
      SetCurrent(in key.Key, in value.Value);
      return true;
    }
    return false;
  }

  bool NextReverse()
  {
    while (HasPending || FetchPending())
    {
      var key = PendingKey.Key;
      var hasVisible = false;
      VersionedValue<TValue> visible = default;
      // The versions of a key come oldest first; the last visible one is the newest.
      do
      {
        Extend(in PendingKey.Key);
        if (Transaction.IsVisible(PendingKey.Version))
        {
          hasVisible = true;
          visible = PendingValue;
        }
      } while (FetchPending() && Comparer.Compare(in PendingKey.Key, in key) == 0);

      if (hasVisible && visible.Kind == VersionedValueKind.Value)
      {
        SetCurrent(in key, in visible.Value);
        return true;
      }
    }
    return false;
  }

  bool FetchPending()
  {
    // System records sort before the data records, so they end a reverse iteration.
    if (NextStored() && Inner.CurrentKey.Kind == VersionedKeyKind.Data)
    {
      PendingKey = Inner.CurrentKey;
      PendingValue = Inner.CurrentValue;
      HasPending = true;
      return true;
    }
    HasPending = false;
    return false;
  }

  void SetCurrent(in TKey key, in TValue value)
  {
    currentKey = key;
    currentValue = value;
    HasCurrent = true;
  }

  /// <summary>
  /// An iterator of an ended transaction reports the end of the transaction,
  /// and a disposed one of an active transaction reports its disposal.
  /// </summary>
  void EnsureUsable()
  {
    Transaction.EnsureActive();
    ObjectDisposedException.ThrowIf(isDisposed, this);
  }

  void ResetPosition()
  {
    IsPositioned = true;
    IsExhausted = false;
    HasCurrent = false;
    HasGroup = false;
    IsGroupResolved = false;
    HasPending = false;
  }

  /// <summary>
  /// Starts the range of a new position. It joins the read set
  /// when <see cref="Next"/> reads from it.
  /// </summary>
  void StartRange(in KeyRange<TKey> range)
  {
    Range = range;
    RangeIndex = -1;
  }

  void Extend(in TKey key)
  {
    if (IsReverse)
    {
      Range.Low = key;
      Range.HasLow = true;
    }
    else
    {
      Range.High = key;
      Range.HasHigh = true;
    }
  }

  void PublishRange()
  {
    if (!Transaction.TracksReads)
      return;
    if (RangeIndex < 0)
      RangeIndex = Transaction.AddReadRange(in Range);
    else
      Transaction.UpdateReadRange(RangeIndex, in Range);
  }

  public void Dispose()
  {
    if (isDisposed)
      return;
    isDisposed = true;
    Inner.Dispose();
    Transaction.OnIteratorDisposed(this);
  }
}
