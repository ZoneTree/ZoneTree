using System.Text.Json.Serialization;
using ZoneTree.Options;

namespace ZoneTree.Core;

public sealed class ZoneTreeMeta
{
  public string Version { get; set; }

  public string ComparerType { get; set; }

  public string KeyHasherType { get; set; }

  public string KeyType { get; set; }

  public string ValueType { get; set; }

  public string KeySerializerType { get; set; }

  public string ValueSerializerType { get; set; }

  public int MutableSegmentMaxItemCount { get; set; }

  public int MutableSegmentBloomFilterBitsPerItem { get; set; }

  public int DiskSegmentMaxItemCount { get; set; } = 20_000_000;

  public WriteAheadLogOptions WriteAheadLogOptions { get; set; }

  public DiskSegmentOptions DiskSegmentOptions { get; set; }

  public long MutableSegment { get; set; }

  public IReadOnlyList<long> ReadOnlySegments { get; set; }

  public long DiskSegment { get; set; }

  public IReadOnlyList<long> BottomSegments { get; set; }

  /// <summary>
  /// The persisted producer high-water mark for operation indexes.
  /// </summary>
  /// <remarks>
  /// This value is not a version of the whole database, segment tree, or merge
  /// shape. Operation indexes are used by consumers such as replication to
  /// compare updates for the same key and ignore stale writes. Persisting the
  /// maximum issued index prevents a restarted producer from assigning a lower
  /// index to a later write for that key.
  /// </remarks>
  public long MaximumOpIndex { get; set; }

  /// <summary>
  /// The generation of the meta WAL records that this metadata does not include.
  /// </summary>
  /// <remarks>
  /// Saving the metadata and clearing the meta WAL are two steps. After a
  /// power loss between them, the meta WAL still holds the records that the
  /// metadata includes. Their older generation tells loading to skip them.
  /// Zero means an older version saved the metadata and its records.
  /// </remarks>
  public long MetaWalGeneration { get; set; }

  [JsonIgnore]
  public bool HasDiskSegment => DiskSegment != 0 || BottomSegments?.Count > 0;
}
