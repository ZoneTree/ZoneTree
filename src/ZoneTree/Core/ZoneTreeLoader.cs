using System.Collections.Concurrent;
using System.Text;
using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Segments;
using ZoneTree.Segments.Disk;
using ZoneTree.Segments.InMemory;
using ZoneTree.Segments.MultiPart;
using ZoneTree.Segments.NullDisk;
using ZoneTree.WAL;

namespace ZoneTree.Core;

public sealed class ZoneTreeLoader<TKey, TValue>
{
  const string LegacyRootNamespace = "Tenray.ZoneTree.";

  const string RootNamespace = "ZoneTree.";

  static readonly Version LegacyNamespaceMigrationVersion = new("1.8.7.0");

  ZoneTreeOptions<TKey, TValue> Options { get; }

  ZoneTreeMeta ZoneTreeMeta;

  IMutableSegment<TKey, TValue> MutableSegment { get; set; }

  IReadOnlySegment<TKey, TValue>[] ReadOnlySegments;

  IDiskSegment<TKey, TValue> DiskSegment { get; set; }

  IReadOnlyList<IDiskSegment<TKey, TValue>> BottomSegments;

  long maximumSegmentId = 1;
  public ZoneTreeLoader(ZoneTreeOptions<TKey, TValue> options)
  {
    Options = options;
    Options.Validate();
  }

  public bool ZoneTreeMetaExists => ZoneTreeMetaWAL<TKey, TValue>.Exists(Options);

  void LoadZoneTreeMeta()
  {
    ZoneTreeMeta = ZoneTreeMetaWAL<TKey, TValue>
        .LoadZoneTreeMetaWithoutWALRecords(Options.RandomAccessDeviceManager);
    ValidateZoneTreeMeta();
  }

  void SetMaximumSegmentId(long newId)
  {
    maximumSegmentId = Math.Max(maximumSegmentId, newId);
  }

  void ValidateZoneTreeMeta()
  {
    var version = ZoneTreeMeta.Version;
    if (string.IsNullOrWhiteSpace(version) ||
        Version.Parse(version) < new Version("1.4.9"))
      throw new ExistingDatabaseVersionIsNotCompatibleException(
          Version.Parse(version),
          ZoneTreeInfo.ProductVersion);

    var isLegacyNamespaceMigrated =
        Version.Parse(version) < LegacyNamespaceMigrationVersion &&
        NormalizeLegacyNamespaceInZoneTreeMeta();

    if (!string.Equals(
        ZoneTreeMeta.KeyType,
        typeof(TKey).SimplifiedFullName(),
        StringComparison.Ordinal))
      throw new TreeKeyTypeMismatchException(
          ZoneTreeMeta.KeyType,
          typeof(TKey).SimplifiedFullName());

    if (!string.Equals(
        ZoneTreeMeta.ValueType,
        typeof(TValue).SimplifiedFullName(),
        StringComparison.Ordinal))
      throw new TreeValueTypeMismatchException(
          ZoneTreeMeta.ValueType,
          typeof(TValue).SimplifiedFullName());

    if (!string.Equals(
        ZoneTreeMeta.ComparerType,
        Options.Comparer.GetType().SimplifiedFullName(),
        StringComparison.Ordinal))
      throw new TreeComparerMismatchException(
          ZoneTreeMeta.ComparerType,
          Options.Comparer.GetType().SimplifiedFullName());

    if (!string.Equals(
        ZoneTreeMeta.KeySerializerType,
        Options.KeySerializer.GetType().SimplifiedFullName(),
        StringComparison.Ordinal))
      throw new TreeKeySerializerTypeMismatchException(
          ZoneTreeMeta.KeySerializerType,
          Options.KeySerializer.GetType().SimplifiedFullName());

    if (!string.Equals(
        ZoneTreeMeta.ValueSerializerType,
        Options.ValueSerializer.GetType().SimplifiedFullName(),
        StringComparison.Ordinal))
      throw new TreeValueSerializerTypeMismatchException(
          ZoneTreeMeta.ValueSerializerType,
          Options.ValueSerializer.GetType().SimplifiedFullName());

    if (isLegacyNamespaceMigrated)
    {
      Options.Logger?.LogWarning(
          "ZoneTree metadata type names were migrated from Tenray.ZoneTree namespace to ZoneTree namespace.");
    }
  }

  static string NormalizeLegacyNamespace(string typeName)
  {
    return typeName?.Replace(
        LegacyRootNamespace,
        RootNamespace,
        StringComparison.Ordinal);
  }

  bool NormalizeLegacyNamespaceInZoneTreeMeta()
  {
    var keyType = NormalizeLegacyNamespace(ZoneTreeMeta.KeyType);
    var valueType = NormalizeLegacyNamespace(ZoneTreeMeta.ValueType);
    var comparerType = NormalizeLegacyNamespace(ZoneTreeMeta.ComparerType);
    var keySerializerType = NormalizeLegacyNamespace(ZoneTreeMeta.KeySerializerType);
    var valueSerializerType = NormalizeLegacyNamespace(ZoneTreeMeta.ValueSerializerType);

    if (keyType == ZoneTreeMeta.KeyType &&
        valueType == ZoneTreeMeta.ValueType &&
        comparerType == ZoneTreeMeta.ComparerType &&
        keySerializerType == ZoneTreeMeta.KeySerializerType &&
        valueSerializerType == ZoneTreeMeta.ValueSerializerType)
      return false;

    ZoneTreeMeta.KeyType = keyType;
    ZoneTreeMeta.ValueType = valueType;
    ZoneTreeMeta.ComparerType = comparerType;
    ZoneTreeMeta.KeySerializerType = keySerializerType;
    ZoneTreeMeta.ValueSerializerType = valueSerializerType;
    return true;
  }

  void LoadZoneTreeMetaWAL()
  {
    using var metaWal = new ZoneTreeMetaWAL<TKey, TValue>(Options, false);
    var records = metaWal.GetRecordsOfGeneration(ZoneTreeMeta.MetaWalGeneration);
    var readOnlySegments = ZoneTreeMeta.ReadOnlySegments.ToList();
    var bottomSegments = ZoneTreeMeta.BottomSegments?.ToList() ?? new List<long>();
    var maximumOpIndex = ZoneTreeMeta.MaximumOpIndex;
    foreach (var record in records)
    {
      var segmentId = record.SegmentId;
      if (record.Operation != MetaWalOperation.EnqueueMaximumOpIndex)
        SetMaximumSegmentId(segmentId);
      switch (record.Operation)
      {
        case MetaWalOperation.NewMutableSegment:
          ZoneTreeMeta.MutableSegment = segmentId;
          break;
        case MetaWalOperation.NewDiskSegment:
          ZoneTreeMeta.DiskSegment = segmentId;
          break;
        case MetaWalOperation.EnqueueReadOnlySegment:
          readOnlySegments.Insert(0, segmentId);
          break;
        case MetaWalOperation.DequeueReadOnlySegment:
          if (readOnlySegments.Count == 0 ||
              readOnlySegments.Last() != segmentId)
            throw new WriteAheadLogCorruptionException(segmentId, null);
          readOnlySegments.RemoveAt(readOnlySegments.Count - 1);
          break;
        case MetaWalOperation.EnqueueBottomSegment:
          bottomSegments.Insert(0, segmentId);
          break;
        case MetaWalOperation.DequeueBottomSegment:
          if (bottomSegments.Count == 0 ||
              bottomSegments.Last() != segmentId)
            throw new WriteAheadLogCorruptionException(segmentId, null);
          bottomSegments.RemoveAt(bottomSegments.Count - 1);
          break;
        case MetaWalOperation.InsertBottomSegment:
          bottomSegments.Insert(record.Index, segmentId);
          break;
        case MetaWalOperation.DeleteBottomSegment:
          bottomSegments.Remove(segmentId);
          break;
        case MetaWalOperation.EnqueueMaximumOpIndex:
          maximumOpIndex = Math.Max(maximumOpIndex, record.SegmentId);
          break;
      }
    }
    ZoneTreeMeta.ReadOnlySegments = readOnlySegments;
    ZoneTreeMeta.BottomSegments = bottomSegments;
    ZoneTreeMeta.MaximumOpIndex = maximumOpIndex;
    metaWal.SaveMetaData(
        ZoneTreeMeta,
        ZoneTreeMeta.MutableSegment,
        ZoneTreeMeta.DiskSegment,
        readOnlySegments.ToArray(),
        bottomSegments.ToArray());
    ValidateSegmentOrder();
  }

  void ValidateSegmentOrder()
  {
    var index = long.MaxValue;
    foreach (var ros in ZoneTreeMeta.ReadOnlySegments)
    {
      if (index <= ros)
        throw new ZoneTreeMetaCorruptionException();
      index = ros;
    }
  }

  IWriteAheadLog<TKey, TValue> LoadMutableSegment(long maximumOpIndex,
      bool collectGarbage)
  {
    var loader = new MutableSegmentLoader<TKey, TValue>(Options);
    MutableSegment = loader
        .LoadMutableSegment(
            ZoneTreeMeta.MutableSegment,
            maximumOpIndex,
            collectGarbage,
            out var wal);
    return wal;
  }

  void SeparateMutableSegmentFromReadOnlySegments()
  {
    // MoveMutableSegmentForward records EnqueueReadOnlySegment and
    // NewMutableSegment separately. A crash between them leaves the mutable
    // segment id in the read-only segments. That WAL belongs to the read-only
    // segment now, so continue with an empty mutable segment under a new id.
    if (!ZoneTreeMeta.ReadOnlySegments.Contains(ZoneTreeMeta.MutableSegment))
      return;

    SetMaximumSegmentId(maximumSegmentId + 1);
    ZoneTreeMeta.MutableSegment = maximumSegmentId;

    // Persist the new id before the mutable segment writes to its WAL.
    // Otherwise a later load could pick another id and orphan that WAL.
    using var metaWal = new ZoneTreeMetaWAL<TKey, TValue>(Options, false);
    metaWal.SaveMetaData(
        ZoneTreeMeta,
        ZoneTreeMeta.MutableSegment,
        ZoneTreeMeta.DiskSegment,
        ZoneTreeMeta.ReadOnlySegments.ToArray(),
        ZoneTreeMeta.BottomSegments?.ToArray() ?? Array.Empty<long>());
  }

  void SaveMaximumOpIndex(long maximumOpIndex)
  {
    if (maximumOpIndex <= ZoneTreeMeta.MaximumOpIndex)
      return;

    // Operation indexes are compared per key by consumers such as replication.
    // Persist the producer high-water mark before loaded segment WALs can be
    // removed or compacted, so a later write for the same key cannot restart
    // with a lower operation index.
    ZoneTreeMeta.MaximumOpIndex = maximumOpIndex;
    using var metaWal = new ZoneTreeMetaWAL<TKey, TValue>(Options, false);
    metaWal.SaveMetaData(
        ZoneTreeMeta,
        ZoneTreeMeta.MutableSegment,
        ZoneTreeMeta.DiskSegment,
        ZoneTreeMeta.ReadOnlySegments.ToArray(),
        ZoneTreeMeta.BottomSegments?.ToArray() ?? Array.Empty<long>());
  }

  long LoadReadOnlySegments()
  {
    long maximumOpIndex = 0;
    var segments = ZoneTreeMeta.ReadOnlySegments;
    var map = new ConcurrentDictionary<long, IReadOnlySegment<TKey, TValue>>();

    var loader = new ReadOnlySegmentLoader<TKey, TValue>(Options);
    Parallel.ForEach(segments, (segment) =>
    {
      var ros = loader.LoadReadOnlySegment(segment);
      map.TryAdd(segment, ros);
    });
    foreach (var ros in map.Values)
    {
      maximumOpIndex = Math.Max(ros.MaximumOpIndex, maximumOpIndex);
    }
    ReadOnlySegments = segments.Select(x => map[x]).ToArray();
    return maximumOpIndex;
  }

  void LoadDiskSegment()
  {
    var segmentId = ZoneTreeMeta.DiskSegment;
    if (segmentId == 0)
    {
      DiskSegment = new NullDiskSegment<TKey, TValue>();
      return;
    }
    DiskSegment = DiskSegmentFactory.CreateDiskSegment(segmentId, Options);
  }

  void LoadBottomSegments()
  {
    var segments = ZoneTreeMeta.BottomSegments;
    var map = new ConcurrentDictionary<long, IDiskSegment<TKey, TValue>>();

    Parallel.ForEach(segments, (segmentId) =>
    {
      var ds = DiskSegmentFactory.CreateDiskSegment(segmentId, Options);
      map.AddOrUpdate(segmentId, ds, (_, _) => ds);
    });
    BottomSegments = segments.Select(x => map[x]).ToArray();
  }

  void SetMaximumId()
  {
    SetMaximumSegmentId(ZoneTreeMeta.MutableSegment);
    SetMaximumSegmentId(ZoneTreeMeta.DiskSegment);
    SetMaximumSegmentId(MultiPartDiskSegment<TKey, TValue>
        .ReadMaximumSegmentId(ZoneTreeMeta.DiskSegment, Options.RandomAccessDeviceManager));
    var ros = ZoneTreeMeta.ReadOnlySegments;
    var maximumId = ros.Count > 0 ? ros[0] : 0;
    SetMaximumSegmentId(maximumId);
    var bs = ZoneTreeMeta.BottomSegments;
    maximumId = bs.Count > 0 ? bs.Max() : 0;
    SetMaximumSegmentId(maximumId);
    if (maximumId > 0)
      SetMaximumSegmentId(MultiPartDiskSegment<TKey, TValue>
          .ReadMaximumSegmentId(maximumId, Options.RandomAccessDeviceManager));
  }

  public ZoneTree<TKey, TValue> LoadZoneTree()
  {
    LoadZoneTreeMeta();
    LoadZoneTreeMetaWAL();
    SetMaximumId();
    SeparateMutableSegmentFromReadOnlySegments();
    var maximumOpIndex = Math.Max(ZoneTreeMeta.MaximumOpIndex, LoadReadOnlySegments());
    bool collectGarbage = Options.EnableSingleSegmentGarbageCollection && !ZoneTreeMeta.HasDiskSegment && ReadOnlySegments.Length == 0;
    var mutableSegmentWal = LoadMutableSegment(maximumOpIndex, collectGarbage);
    SaveMaximumOpIndex(MutableSegment.OpIndexProvider.LastId);
    if (collectGarbage)
    {
      var len = MutableSegment.Length;
      if (mutableSegmentWal.InitialLength != MutableSegment.Length)
      {
        var keys = new TKey[len];
        var values = new TValue[len];
        var iterator = MutableSegment.GetSeekableIterator();
        var i = 0;
        while (iterator.Next())
        {
          keys[i] = iterator.CurrentKey;
          values[i++] = iterator.CurrentValue;
        }
        mutableSegmentWal.ReplaceWriteAheadLog(keys, values);
      }
    }
    LoadDiskSegment();
    LoadBottomSegments();

    var zoneTree = new ZoneTree<TKey, TValue>(Options, ZoneTreeMeta,
        ReadOnlySegments, MutableSegment, DiskSegment, BottomSegments, maximumSegmentId);
    return zoneTree;
  }
}
