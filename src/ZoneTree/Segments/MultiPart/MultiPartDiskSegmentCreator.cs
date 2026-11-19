using ZoneTree.Core;
using ZoneTree.Options;
using ZoneTree.Segments.Disk;
using ZoneTree.Serializers;

namespace ZoneTree.Segments.MultiPart;

public sealed class MultiPartDiskSegmentCreator<TKey, TValue> : IDiskSegmentCreator<TKey, TValue>
{
  readonly long SegmentId;

  readonly ISerializer<TKey> KeySerializer;

  readonly ISerializer<TValue> ValueSerializer;

  readonly ZoneTreeOptions<TKey, TValue> Options;

  readonly IIncrementalIdProvider IncrementalIdProvider;

  DiskSegmentCreator<TKey, TValue> NextCreator;

  readonly int DiskSegmentMaximumRecordCount;

  readonly int DiskSegmentMinimumRecordCount;

  readonly List<IDiskSegment<TKey, TValue>> Parts = new();

  readonly List<TKey> PartKeys = new();

  readonly List<TValue> PartValues = new();

  readonly Random Random = Random.Shared;

  TKey LastAppendedKey;

  TValue LastAppendedValue;

  bool IsReadOnlyDiskSegmentCreated;

  bool IsDropped;

  bool IsDisposed;

  public HashSet<long> AppendedPartSegmentIds { get; } = new();

  public int CurrentPartLength => NextCreator.Length;

  public bool CanSkipCurrentPart =>
      NextCreator.Length == 0 ||
      NextCreator.Length >= DiskSegmentMinimumRecordCount;

  public int NextMaximumRecordCount;

  public MultiPartDiskSegmentCreator(
      ZoneTreeOptions<TKey, TValue> options,
      IIncrementalIdProvider incrementalIdProvider
      )
  {
    SegmentId = incrementalIdProvider.NextId();
    KeySerializer = options.KeySerializer;
    ValueSerializer = options.ValueSerializer;
    Options = options;
    IncrementalIdProvider = incrementalIdProvider;
    NextCreator = new(options, incrementalIdProvider);
    DiskSegmentMaximumRecordCount = Options.DiskSegmentOptions.MaximumRecordCount;
    DiskSegmentMinimumRecordCount = Options.DiskSegmentOptions.MinimumRecordCount;
    SetNextMaximumRecordCount();
  }

  void SetNextMaximumRecordCount()
  {
    NextMaximumRecordCount = Random.Next(
        Options.DiskSegmentOptions.MinimumRecordCount,
        Options.DiskSegmentOptions.MaximumRecordCount);
  }

  public void Append(TKey key, TValue value, IteratorPosition iteratorPosition)
  {
    var len = NextCreator.Length;
    if (len == 0)
    {
      PartKeys.Add(key);
      PartValues.Add(value);
    }
    else if (len == NextMaximumRecordCount - 1)
    {
      if (iteratorPosition == IteratorPosition.MiddleOfAPart &&
          len < DiskSegmentMaximumRecordCount)
      {
        ++NextMaximumRecordCount;
      }
      else
      {
        SetNextMaximumRecordCount();
        PartKeys.Add(key);
        PartValues.Add(value);
        NextCreator.Append(key, value, iteratorPosition);
        var part = NextCreator.CreateReadOnlyDiskSegment();
        Parts.Add(part);
        NextCreator = new(Options, IncrementalIdProvider);
        return;
      }
    }
    NextCreator.Append(key, value, iteratorPosition);
    LastAppendedKey = key;
    LastAppendedValue = value;
  }

  public void Append(
      IDiskSegment<TKey, TValue> part,
      TKey key1,
      TKey key2,
      TValue value1,
      TValue value2)
  {
    if (NextCreator.Length > 0)
    {
      PartKeys.Add(LastAppendedKey);
      PartValues.Add(LastAppendedValue);
      var currentPart = NextCreator.CreateReadOnlyDiskSegment();
      Parts.Add(currentPart);
      NextCreator = new(Options, IncrementalIdProvider);
    }
    AppendedPartSegmentIds.Add(part.SegmentId);
    Parts.Add(part);
    PartKeys.Add(key1);
    PartKeys.Add(key2);
    PartValues.Add(value1);
    PartValues.Add(value2);
  }

  public IDiskSegment<TKey, TValue> CreateReadOnlyDiskSegment()
  {
    ThrowIfNotWritable();

    if (NextCreator.Length == 0)
    {
      NextCreator.DropDiskSegment();
      NextCreator = null;
    }
    else
    {
      PartKeys.Add(LastAppendedKey);
      PartValues.Add(LastAppendedValue);
      var part = NextCreator.CreateReadOnlyDiskSegment();
      Parts.Add(part);
      NextCreator = null;
    }

    WriteMultiDiskSegment();

    var diskSegment = new MultiPartDiskSegment<TKey, TValue>(
        SegmentId,
        Options,
        Parts,
        PartKeys.ToArray(),
        PartValues.ToArray());
    IsReadOnlyDiskSegmentCreated = true;
    return diskSegment;
  }

  void WriteMultiDiskSegment()
  {
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    WriteParts(bw);
    WriteKeys(bw);
    WriteValues(bw);
    bw.Flush();
    var compressionMethod = MultiPartDiskSegment<TKey, TValue>
        .MultiPartHeaderCompressionMethod;
    var compressionLevel = MultiPartDiskSegment<TKey, TValue>
        .MultiPartHeaderCompressionLevel;
    using var multiDevice = Options.RandomAccessDeviceManager.
        CreateWritableDevice(
                SegmentId,
                DiskSegmentConstants.MultiPartDiskSegmentCategory,
                isCompressed: false,
                compressionBlockSize: 0,
                deleteIfExists: false,
                backupIfDelete: false,
                compressionMethod,
                compressionLevel);
    var compressedBytes = MultiPartMetadataCodec.Encode(ms.ToArray());
    multiDevice.AppendBytesReturnPosition(compressedBytes.Span);
    Options.RandomAccessDeviceManager
        .RemoveWritableDevice(SegmentId, DiskSegmentConstants.MultiPartDiskSegmentCategory);
  }

  void WriteParts(BinaryWriter bw)
  {
    var len = Parts.Count;
    bw.Write(len);
    for (var i = 0; i < len; ++i)
      bw.Write(Parts[i].SegmentId);
  }

  void WriteKeys(BinaryWriter bw)
  {
    var len = Parts.Count * 2;
    bw.Write(len);
    for (var i = 0; i < len; ++i)
    {
      var a = i++;
      var b = i;
      var k1 = PartKeys[a];
      var bytes = KeySerializer.Serialize(k1);
      bw.Write(bytes.Length);
      bw.Write(bytes.Span);

      var k2 = PartKeys[b];
      bytes = KeySerializer.Serialize(k2);
      bw.Write(bytes.Length);
      bw.Write(bytes.Span);
    }
  }

  void WriteValues(BinaryWriter bw)
  {
    var len = Parts.Count * 2;
    bw.Write(len);
    for (var i = 0; i < len; ++i)
    {
      var a = i++;
      var b = i;
      var v1 = PartValues[a];
      var bytes = ValueSerializer.Serialize(v1);
      bw.Write(bytes.Length);
      bw.Write(bytes.Span);

      var v2 = PartValues[b];
      bytes = ValueSerializer.Serialize(v2);
      bw.Write(bytes.Length);
      bw.Write(bytes.Span);
    }
  }

  public void DropDiskSegment()
  {
    if (IsDropped)
      return;
    ObjectDisposedException.ThrowIf(
        IsDisposed,
        this);
    if (IsReadOnlyDiskSegmentCreated)
      throw new InvalidOperationException(
          "Cannot drop a disk segment creator after ownership has been transferred.");

    foreach (var part in Parts)
    {
      if (AppendedPartSegmentIds.Contains(part.SegmentId))
        continue;
      part.Drop();
    }

    NextCreator?.DropDiskSegment();
    NextCreator = null;

    var randomAccessDeviceManager = Options.RandomAccessDeviceManager;
    if (!randomAccessDeviceManager.DeviceExists(
        SegmentId,
        DiskSegmentConstants.MultiPartDiskSegmentCategory,
        isCompressed: false))
    {
      IsDropped = true;
      return;
    }

    using var multiDevice = randomAccessDeviceManager.GetReadOnlyDevice(
        SegmentId,
        DiskSegmentConstants.MultiPartDiskSegmentCategory,
        isCompressed: false,
        compressionBlockSize: 0,
        MultiPartDiskSegment<TKey, TValue>.MultiPartHeaderCompressionMethod,
        MultiPartDiskSegment<TKey, TValue>.MultiPartHeaderCompressionLevel);

    multiDevice.Delete();
    randomAccessDeviceManager.RemoveReadOnlyDevice(
        SegmentId,
        DiskSegmentConstants.MultiPartDiskSegmentCategory);
    IsDropped = true;
  }

  public void Dispose()
  {
    if (IsReadOnlyDiskSegmentCreated || IsDropped || IsDisposed)
      return;

    foreach (var part in Parts)
    {
      if (AppendedPartSegmentIds.Contains(part.SegmentId))
        continue;
      part.Dispose();
    }

    NextCreator?.Dispose();
    NextCreator = null;
    IsDisposed = true;
  }

  void ThrowIfNotWritable()
  {
    if (IsReadOnlyDiskSegmentCreated)
      throw new InvalidOperationException(
          "The disk segment creator has already transferred ownership.");
    ObjectDisposedException.ThrowIf(
        IsDropped,
        this);
    ObjectDisposedException.ThrowIf(
        IsDisposed,
        this);
  }
}
