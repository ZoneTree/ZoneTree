using ZoneTree.WAL;
using ZoneTree.Collections.BTree.Lock;
using ZoneTree.Logger;
using ZoneTree.Serializers;
using ZoneTree.Comparers;
using ZoneTree.Segments.RandomAccess;
using ZoneTree.PresetTypes;
using ZoneTree.Hashers;

namespace ZoneTree.Options;

/// <summary>
/// A delegate to query key-value pair deletion state.
/// </summary>
/// <typeparam name="TValue">The value type</typeparam>
/// <typeparam name="TKey">The key type</typeparam>
/// <param name="value">Key to be queried</param>
/// <param name="value">Value to be queried</param>
/// <returns>true if the key-value pair is deleted, false otherwise</returns>
public delegate bool IsDeletedDelegate<TKey, TValue>(in TKey key, in TValue value);

/// <summary>
/// A delegate to mark a value deleted.
/// </summary>
/// <typeparam name="TValue">The value type</typeparam>
/// <param name="value">The value to be marked as deleted</param>
public delegate void MarkValueDeletedDelegate<TValue>(ref TValue value);

/// <summary>
/// Represents configuration options of a ZoneTree.
/// </summary>
/// <typeparam name="TKey">The key type</typeparam>
/// <typeparam name="TValue">The value type</typeparam>
public sealed class ZoneTreeOptions<TKey, TValue>
{
  /// <summary>
  /// Mutable segment maximum key-value pair count.
  /// When the maximum count is reached
  /// MoveMutableSegmentForward is called and current mutable segment is enqueued to
  /// the ReadOnlySegments layer.
  /// Larger values keep more records in the mutable segment before it moves forward.
  /// </summary>
  public int MutableSegmentMaxItemCount { get; set; } = DefaultValues.MutableSegmentMaxItemCount;

  /// <summary>
  /// Requested Bloom-filter bits per mutable-segment item. Higher values reduce
  /// false positives but use more memory. The allocation is rounded up to a
  /// power of two. Setting this value to zero disables the mutable-segment
  /// Bloom filter. A key hasher is required when this value is greater than
  /// zero. Valid values are between 0 and 64. Default value is 8.
  /// </summary>
  public int MutableSegmentBloomFilterBitsPerItem { get; set; } =
      DefaultValues.MutableSegmentBloomFilterBitsPerItem;

  /// <summary>
  /// Disk segment maximum key-value pair count.
  /// After a merge creates a disk segment, segments larger than this value are
  /// enqueued into the bottom segments layer.
  /// </summary>
  public int DiskSegmentMaxItemCount { get; set; } = DefaultValues.DiskSegmentMaxItemCount;

  /// <summary>
  /// The key comparer.
  /// </summary>
  public IRefComparer<TKey> Comparer { get; set; }

  /// <summary>
  /// The key hasher. Keys considered equal by <see cref="Comparer"/> must
  /// produce the same hash code. Required when
  /// <see cref="MutableSegmentBloomFilterBitsPerItem"/> is greater than zero.
  /// </summary>
  public IKeyHasher<TKey> KeyHasher { get; set; }

  /// <summary>
  /// The key serializer.
  /// </summary>
  public ISerializer<TKey> KeySerializer { get; set; }

  /// <summary>
  /// The value serializer.
  /// </summary>
  public ISerializer<TValue> ValueSerializer { get; set; }

  /// <summary>
  /// Delegate to query key-value pair deletion state.
  /// </summary>
  public IsDeletedDelegate<TKey, TValue> IsDeleted { get; set; }

  /// <summary>
  /// Delegate to mark value deleted.
  /// </summary>
  public MarkValueDeletedDelegate<TValue> MarkValueDeleted { get; set; }

  /// <summary>
  /// Write Ahead Log Options. The options are used
  /// to create new Write Ahead Logs.
  /// Existing WALs are created with their existing options.
  /// </summary>
  public WriteAheadLogOptions WriteAheadLogOptions { get; set; } = new();

  /// <summary>
  /// Disk Segment options. The options are used
  /// to create new disk segments.
  /// Existing disk segments are created with
  /// their existing options.
  /// </summary>
  public DiskSegmentOptions DiskSegmentOptions { get; set; } = new();

  /// <summary>
  /// Controls lock granularity of in-memory BTree that represents the
  /// mutable segment.
  /// </summary>
  public BTreeLockMode BTreeLockMode { get; set; } = DefaultValues.BTreeLockMode;

  /// <summary>
  /// The B+Tree node size.
  /// Larger values can reduce tree height, but each node holds more keys in memory.
  /// </summary>
  public int BTreeNodeSize { get; set; } = DefaultValues.BTreeNodeSize;

  /// <summary>
  /// The B+Tree leaf size.
  /// Larger values can improve scan locality, but each leaf holds more records in memory.
  /// </summary>
  public int BTreeLeafSize { get; set; } = DefaultValues.BTreeLeafSize;

  /// <summary>
  /// Allows unsafe numeric option values for advanced or test configurations.
  /// Required components, enum values, and compression method/level compatibility
  /// are still validated.
  /// </summary>
  public bool AllowUnsafeOptionValues { get; set; }

  /// <summary>
  /// ZoneTree Logger.
  /// </summary>
  public ILogger Logger { get; set; } = new ConsoleLogger();

  /// <summary>
  /// Tries validate ZoneTree options without throwing an exception.
  /// </summary>
  /// <param name="exception">The exception outcome of validation.</param>
  /// <returns>true if validation succeeds, false otherwise.</returns>
  public bool TryValidate(out Exception exception)
  {
    return ZoneTreeOptionsValidator.TryValidate(this, out exception);
  }

  /// <summary>
  /// Validates the ZoneTree options.
  /// </summary>
  public void Validate()
  {
    if (!TryValidate(out Exception exception))
      throw exception;
  }

  /// <summary>
  /// Configures the write ahead log provider.
  /// </summary>
  public IWriteAheadLogProvider WriteAheadLogProvider { get; set; }

  /// <summary>
  /// Configures the random access device manager.
  /// </summary>
  public IRandomAccessDeviceManager RandomAccessDeviceManager { get; set; }

  /// <summary>
  /// If the ZoneTree contains only a single segment (which is the mutable segment),
  /// there is an opportunity to perform a hard delete of the soft deleted values.
  /// If enabled, the tree performs garbage collection on load if it is applicable.
  /// </summary>
  public bool EnableSingleSegmentGarbageCollection { get; set; }

  /// <summary>
  /// Creates default delete delegates for nullable types if they are not already set.
  /// </summary>
  public void CreateDefaultDeleteDelegates()
  {
    if (IsDeleted == null)
      IsDeleted = ComponentsForKnownTypes.GetIsDeleted<TKey, TValue>();
    if (MarkValueDeleted == null)
      MarkValueDeleted = ComponentsForKnownTypes.GetMarkValueDeleted<TValue>();
  }

  /// <summary>
  /// Disables deletion to be able to insert default values of the value type.
  /// Databases created with this option are not able to delete records.
  /// </summary>
  public void DisableDeletion()
  {
    IsDeleted = (in TKey _, in TValue _) => false;
    MarkValueDeleted = (ref TValue _) => { };
  }
}
