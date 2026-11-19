using System;
using System.Buffers;
using ZoneTree.AbstractFileStream;
using ZoneTree.Collections;
using ZoneTree.Comparers;
using ZoneTree.Exceptions;
using ZoneTree.Hashers;
using ZoneTree.Options;
using ZoneTree.Segments.Block;
using ZoneTree.Segments.Disk;
using ZoneTree.Segments.RandomAccess;
using ZoneTree.Serializers;
using ZoneTree.Synchronization;

namespace ZoneTree.Segments.MultiPart;

public sealed class MultiPartDiskSegment<TKey, TValue> : IDiskSegment<TKey, TValue>
{
  public const CompressionMethod MultiPartHeaderCompressionMethod
      = MultiPartMetadataCodec.CurrentCompressionMethod;

  public const int MultiPartHeaderCompressionLevel
      = MultiPartMetadataCodec.CurrentCompressionLevel;

  public long SegmentId { get; }

  readonly IRefComparer<TKey> Comparer;

  readonly ISerializer<TKey> KeySerializer;

  readonly ISerializer<TValue> ValueSerializer;

  LifecycleLeaseState IteratorLeaseState;

  readonly Lock DropLock = new();

  bool HasPendingDropPlan;

  HashSet<long> PendingDropExcludedPartIds;

  readonly IReadOnlyList<IDiskSegment<TKey, TValue>> Parts;

  readonly TKey[] PartKeys;

  readonly TValue[] PartValues;

  readonly ZoneTreeOptions<TKey, TValue> Options;

  ReadEntriesDelegate<TKey, TValue> ReadEntriesHandler;

  public long Length { get; }

  public long MaximumOpIndex => 0;

  public bool IsFullyFrozen => true;

  public bool IsIterativeIndexReader => false;

  public int ReadBufferCount => 0;

  public Action<IDiskSegment<TKey, TValue>, Exception> DropFailureReporter { get; set; }

  public MultiPartDiskSegment(
      long segmentId,
      ZoneTreeOptions<TKey, TValue> options)
  {
    SegmentId = segmentId;
    Options = options;
    Comparer = options.Comparer;
    KeySerializer = options.KeySerializer;
    ValueSerializer = options.ValueSerializer;

    var randomDeviceManager = options.RandomAccessDeviceManager;
    using var diskSegmentListDevice = randomDeviceManager
            .GetReadOnlyDevice(
                segmentId,
                DiskSegmentConstants.MultiPartDiskSegmentCategory,
                isCompressed: false,
                compressionBlockSize: 0,
                MultiPartHeaderCompressionMethod,
                MultiPartHeaderCompressionLevel);

    if (diskSegmentListDevice.Length > int.MaxValue)
      throw new DataIsTooBigToLoadAtOnceException(
          diskSegmentListDevice.Length, int.MaxValue);

    var len = (int)diskSegmentListDevice.Length;
    var compressedBytes = diskSegmentListDevice.GetBytes(0, len);
    var bytes = MultiPartMetadataCodec.Decode(compressedBytes);
    using var pin = bytes.Pin();
    using var ms = bytes.ToReadOnlyStream(pin);
    using var br = new BinaryReader(ms);
    Parts = ReadParts(options, br);
    PartKeys = ReadKeys(br);
    PartValues = ReadValues(br);
    Length = CalculateLength();
  }

  public static long ReadMaximumSegmentId(
      long segmentId,
      IRandomAccessDeviceManager randomDeviceManager)
  {
    var category = DiskSegmentConstants.MultiPartDiskSegmentCategory;
    if (!randomDeviceManager.DeviceExists(segmentId, category, false))
      return 0;
    using var diskSegmentListDevice = randomDeviceManager
            .GetReadOnlyDevice(
                segmentId,
                category,
                isCompressed: false,
                compressionBlockSize: 0,
                MultiPartHeaderCompressionMethod,
                MultiPartHeaderCompressionLevel);

    if (diskSegmentListDevice.Length > int.MaxValue)
      throw new DataIsTooBigToLoadAtOnceException(
          diskSegmentListDevice.Length, int.MaxValue);

    var len = (int)diskSegmentListDevice.Length;
    var compressedBytes = diskSegmentListDevice.GetBytes(0, len);
    var bytes = MultiPartMetadataCodec.Decode(compressedBytes);

    using var pin = bytes.Pin();
    using var ms = bytes.ToReadOnlyStream(pin);
    using var br = new BinaryReader(ms);

    var partCount = br.ReadInt32();
    var parts = new IDiskSegment<TKey, TValue>[partCount];
    var result = segmentId;
    for (var i = 0; i < partCount; ++i)
    {
      var partSegmentId = br.ReadInt64();
      result = Math.Max(partSegmentId, result);
    }
    randomDeviceManager.RemoveReadOnlyDevice(segmentId, category);
    return result;
  }

  public MultiPartDiskSegment(
      long segmentId,
      ZoneTreeOptions<TKey, TValue> options,
      IReadOnlyList<IDiskSegment<TKey, TValue>> parts,
      TKey[] partKeys,
      TValue[] partValues)
  {
    SegmentId = segmentId;
    Options = options;
    Comparer = options.Comparer;
    KeySerializer = options.KeySerializer;
    ValueSerializer = options.ValueSerializer;
    Parts = parts;
    PartKeys = partKeys;
    PartValues = partValues;
    Length = CalculateLength();
  }

  long CalculateLength()
  {
    var len = Parts.Count;
    var parts = Parts;
    long result = 0;
    for (var i = 0; i < len; ++i)
      result += parts[i].Length;
    return result;
  }

  static IDiskSegment<TKey, TValue>[] ReadParts(
      ZoneTreeOptions<TKey, TValue> options,
      BinaryReader br)
  {
    var partCount = br.ReadInt32();
    var parts = new IDiskSegment<TKey, TValue>[partCount];
    for (var i = 0; i < partCount; ++i)
    {
      var partSegmentId = br.ReadInt64();
      var part = DiskSegmentFactory.CreateDiskSegment(partSegmentId, options);
      parts[i] = part;
    }
    return parts;
  }

  TKey[] ReadKeys(BinaryReader br)
  {
    var keyCount = br.ReadInt32();
    var keys = new TKey[keyCount];
    for (var i = 0; i < keyCount; ++i)
    {
      var keyLength = br.ReadInt32();
      var bytes = br.ReadBytes(keyLength);
      keys[i] = KeySerializer.Deserialize(bytes);
    }
    return keys;
  }

  TValue[] ReadValues(BinaryReader br)
  {
    var valueCount = br.ReadInt32();
    var values = new TValue[valueCount];
    for (var i = 0; i < valueCount; ++i)
    {
      var valueLength = br.ReadInt32();
      var bytes = br.ReadBytes(valueLength);
      values[i] = ValueSerializer.Deserialize(bytes);
    }
    return values;
  }

  public IDiskSegment<TKey, TValue> GetPart(int partIndex)
  {
    return Parts[partIndex];
  }

  public TKey[] GetFirstKeysOfEveryPart()
  {
    var len = PartKeys.Length;
    var keys = new TKey[len / 2];
    for (var i = 0; i < len; i += 2)
      keys[i / 2] = PartKeys[i];
    return keys;
  }

  public TKey[] GetLastKeysOfEveryPart()
  {
    var len = PartKeys.Length;
    var keys = new TKey[len / 2];
    for (var i = 0; i < len; i += 2)
      keys[i / 2] = PartKeys[i + 1];
    return keys;
  }

  public TValue[] GetLastValuesOfEveryPart()
  {
    var len = PartValues.Length;
    var values = new TValue[len / 2];
    for (var i = 0; i < len; i += 2)
      values[i / 2] = PartValues[i + 1];
    return values;
  }

  public void AttachIterator()
  {
    if (!IteratorLeaseState.TryAcquire())
      throw new InvalidOperationException(
          "Cannot attach an iterator after disk segment retirement has started.");
  }

  public bool ContainsKey(in TKey key, ref KeyHashProvider<TKey> keyHashProvider)
  {
    var sparseArrayLength = PartKeys.Length;
    (var left, var found) = SearchLastSmallerOrEqualPositionInSparseArray(in key);
    if (found)
      return true;
    if (left == -1 || left == sparseArrayLength - 1)
      return false;

    var partIndex = left / 2;
    return Parts[partIndex].ContainsKey(in key, ref keyHashProvider);
  }

  public void Dispose()
  {
    ReleaseResources();
  }

  public void Drop()
  {
    RequestDrop(excludedPartIds: null);
  }

  public void Drop(HashSet<long> excludedPartIds)
  {
    RequestDrop(excludedPartIds);
  }

  void RequestDrop(HashSet<long> excludedPartIds)
  {
    lock (DropLock)
    {
      if (!HasPendingDropPlan)
      {
        HasPendingDropPlan = true;
        PendingDropExcludedPartIds = excludedPartIds is null
            ? null
            : [.. excludedPartIds];
      }

      if (IteratorLeaseState.RequestRetirement())
        CompleteDrop(PendingDropExcludedPartIds, reportFailure: false);
    }
  }

  void CompleteDrop(
      HashSet<long> excludedPartIds,
      bool reportFailure)
  {
    try
    {
      var len = Parts.Count;
      for (var i = 0; i < len; ++i)
      {
        var part = Parts[i];
        if (excludedPartIds?.Contains(part.SegmentId) == true)
          continue;
        part.Drop();
      }

      DropMeta();
      IteratorLeaseState.CompleteRetirement();
      PendingDropExcludedPartIds = null;
    }
    catch (Exception e)
    {
      IteratorLeaseState.CancelRetirementCompletion();
      if (!reportFailure)
        throw;
      DropFailureReporter?.Invoke(this, e);
    }
  }

  void DropMeta()
  {
    var randomDeviceManager = Options.RandomAccessDeviceManager;
    using var diskSegmentListDevice = randomDeviceManager
            .GetReadOnlyDevice(
                SegmentId,
                DiskSegmentConstants.MultiPartDiskSegmentCategory,
                isCompressed: false,
                compressionBlockSize: 0,
                MultiPartHeaderCompressionMethod,
                MultiPartHeaderCompressionLevel);

    diskSegmentListDevice.Delete();
    randomDeviceManager
        .RemoveReadOnlyDevice(SegmentId, DiskSegmentConstants.MultiPartDiskSegmentCategory);
  }

  public long GetFirstGreaterOrEqualPosition(in TKey key)
  {
    var sparseArrayLength = PartKeys.Length;
    (var right, var found) = SearchFirstGreaterOrEqualPositionInSparseArray(in key);
    var partIndex = right / 2;
    var diff = right % 2 == 0 ? 0 : 1;
    if (found)
    {
      long off2 = 0;
      var len = partIndex + diff;
      for (var i = 0; i < len; ++i)
        off2 += Parts[i].Length;
      return off2 - diff;
    }
    if (right == sparseArrayLength)
      return Length;

    if (diff == 0)
    {
      long off2 = 0;
      var len = partIndex;
      for (var i = 0; i < len; ++i)
        off2 += Parts[i].Length;
      return off2;
    }

    var position = Parts[partIndex].GetFirstGreaterOrEqualPosition(in key);
    if (position == Parts[partIndex].Length)
      return Length;

    long off = 0;
    for (var i = 0; i < partIndex; ++i)
      off += Parts[i].Length;
    return off + position;
  }

  public IIndexedReader<TKey, TValue> GetIndexedReader()
  {
    return this;
  }

  public TKey GetKey(long index)
  {
    long off = 0;
    var partIndex = 0;
    var len = Parts[partIndex].Length;
    while (off + len <= index)
    {
      off += len;
      ++partIndex;
      len = Parts[partIndex].Length;
    }
    var localIndex = index - off;

    if (localIndex == 0)
      return PartKeys[partIndex * 2];
    if (localIndex == len - 1)
      return PartKeys[partIndex * 2 + 1];

    var key = Parts[partIndex].GetKey(localIndex);
    return key;
  }

  public TValue GetValue(long index)
  {
    long off = 0;
    var partIndex = 0;
    var len = Parts[partIndex].Length;
    while (off + len <= index)
    {
      off += len;
      ++partIndex;
      len = Parts[partIndex].Length;
    }
    var localIndex = index - off;

    if (localIndex == 0)
      return PartValues[partIndex * 2];
    if (localIndex == len - 1)
      return PartValues[partIndex * 2 + 1];

    return Parts[partIndex].GetValue(localIndex);
  }

  public long GetLastSmallerOrEqualPosition(in TKey key)
  {
    (var left, var found) = SearchLastSmallerOrEqualPositionInSparseArray(in key);
    var partIndex = left / 2;
    var diff = left % 2 == 0 ? 0 : 1;
    if (found)
    {
      long off2 = 0;
      var len = partIndex + diff;
      for (var i = 0; i < len; ++i)
        off2 += Parts[i].Length;
      return off2 - diff;
    }
    if (left == -1)
      return -1;

    if (diff == 1)
    {
      long off2 = 0;
      var len = partIndex;
      for (var i = 0; i <= len; ++i)
        off2 += Parts[i].Length;
      return off2 - 1;
    }

    var position = Parts[partIndex].GetLastSmallerOrEqualPosition(in key);
    if (position == -1)
      return -1;

    long off = 0;
    for (var i = 0; i < partIndex; ++i)
      off += Parts[i].Length;
    return off + position;
  }

  public ISeekableIterator<TKey, TValue> GetSeekableIterator(bool contributeToTheBlockCache)
  {
    return GetSeekableIterator(
        contributeToTheBlockCache,
        IteratorDefaultValues.DiskSegmentPrefetchSize);
  }

  public ISeekableIterator<TKey, TValue> GetSeekableIterator(
      bool contributeToTheBlockCache,
      int prefetchSize)
  {
    if (prefetchSize < 2)
      return new SeekableIterator<TKey, TValue>(this, contributeToTheBlockCache);

    // Create the delegate once per segment instead of once per iterator.
    var readEntries = ReadEntriesHandler ??= ReadEntries;
    return new PrefetchingSeekableIterator<TKey, TValue>(
          this,
          readEntries,
          prefetchSize,
          contributeToTheBlockCache);
  }

  public void InitSparseArray(int size)
  {
    // Don't init sparse array for multi-part segments.
    // Sparse arrays can be individually created per the parts.
  }

  public void LoadIntoMemory()
  {
    var len = Parts.Count;
    for (var i = 0; i < len; ++i)
      Parts[i].LoadIntoMemory();
  }

  public int ReleaseReadBuffers(long ticks)
  {
    var len = Parts.Count;
    var result = 0;
    for (var i = 0; i < len; ++i)
      result += Parts[i].ReleaseReadBuffers(ticks);
    return result;
  }

  public void ReleaseResources()
  {
    var len = Parts.Count;
    for (var i = 0; i < len; ++i)
      Parts[i].ReleaseResources();
  }

  public void DetachIterator()
  {
    if (IteratorLeaseState.Release())
    {
      lock (DropLock)
      {
        if (IteratorLeaseState.TryBeginRetirementCompletion())
          CompleteDrop(PendingDropExcludedPartIds, reportFailure: true);
      }
    }
  }

  public bool TryGet(in TKey key, out TValue value, ref KeyHashProvider<TKey> keyHashProvider)
  {
    var sparseArrayLength = PartKeys.Length;
    (var left, var found) = SearchLastSmallerOrEqualPositionInSparseArray(in key);
    if (found)
    {
      value = PartValues[left];
      return true;
    }
    if (left == -1 || left == sparseArrayLength - 1)
    {
      value = default;
      return false;
    }

    var partIndex = left / 2;
    return Parts[partIndex].TryGet(in key, out value, ref keyHashProvider);
  }

  #region Binary search

  (int index, bool found) SearchLastSmallerOrEqualPositionInSparseArray(in TKey key)
  {
    var list = PartKeys;
    var len = list.Length;
    if (len == 0)
      return (-1, false);

    var position = BinarySearchAlgorithms
        .LastSmallerOrEqualPosition(PartKeys, 0, PartKeys.Length - 1, Comparer, in key);
    if (position == -1)
      return (-1, false);
    var exactMatch = Comparer.Compare(PartKeys[position], in key) == 0;
    return (position, exactMatch);
  }

  (int index, bool found) SearchFirstGreaterOrEqualPositionInSparseArray(in TKey key)
  {
    var list = PartKeys;
    var len = list.Length;
    if (len == 0)
      return (0, false);

    var position = BinarySearchAlgorithms
        .FirstGreaterOrEqualPosition(PartKeys, 0, PartKeys.Length - 1, Comparer, in key);
    if (position == len)
      return (len, false);
    var exactMatch = Comparer.Compare(PartKeys[position], in key) == 0;
    return (position, exactMatch);
  }
  #endregion

  public bool IsBeginningOfAPart(long index)
  {
    long off = 0;
    var partIndex = 0;
    var len = Parts[partIndex].Length;
    while (off + len <= index)
    {
      off += len;
      ++partIndex;
      len = Parts[partIndex].Length;
    }
    var localIndex = index - off;

    return localIndex == 0;
  }

  public bool IsEndOfAPart(long index)
  {
    long off = 0;
    var partIndex = 0;
    var len = Parts[partIndex].Length;
    while (off + len <= index)
    {
      off += len;
      ++partIndex;
      len = Parts[partIndex].Length;
    }
    var localIndex = index - off;

    return localIndex == len - 1;
  }

  public int GetPartIndex(long index)
  {
    long off = 0;
    var partIndex = 0;
    var len = Parts[partIndex].Length;
    while (off + len <= index)
    {
      off += len;
      ++partIndex;
      len = Parts[partIndex].Length;
    }
    return partIndex;
  }

  public int GetPartCount() => Parts.Count;

  public DiskSegmentFile[] GetFiles()
  {
    var files = new List<DiskSegmentFile>();
    var deviceManager = Options.RandomAccessDeviceManager;
    var category = DiskSegmentConstants.MultiPartDiskSegmentCategory;
    if (deviceManager.DeviceExists(SegmentId, category, false))
    {
      var path = deviceManager.GetFilePath(SegmentId, category);
      files.Add(new DiskSegmentFile(
          SegmentId,
          path,
          Path.GetFileName(path),
          Length));
    }

    foreach (var part in Parts)
      files.AddRange(part.GetFiles());

    return [.. files];
  }

  public void SetDefaultSparseArray(IReadOnlyList<SparseArrayEntry<TKey, TValue>> defaultSparseArray)
  {
    throw new NotSupportedException("SetDefaultSparseArray is not intended to be called for MultiPartDiskSegment");
  }

  public int ReleaseCircularKeyCacheRecords()
  {
    var len = Parts.Count;
    var result = 0;
    for (var i = 0; i < len; ++i)
      result += Parts[i].ReleaseCircularKeyCacheRecords();
    return result;
  }

  public int ReleaseCircularValueCacheRecords()
  {
    var len = Parts.Count;
    var result = 0;
    for (var i = 0; i < len; ++i)
      result += Parts[i].ReleaseCircularValueCacheRecords();
    return result;
  }

  public TKey GetKey(long index, BlockPin blockPin)
  {
    long off = 0;
    var partIndex = 0;
    var len = Parts[partIndex].Length;
    while (off + len <= index)
    {
      off += len;
      ++partIndex;
      len = Parts[partIndex].Length;
    }
    var localIndex = index - off;

    if (localIndex == 0)
      return PartKeys[partIndex * 2];
    if (localIndex == len - 1)
      return PartKeys[partIndex * 2 + 1];

    var key = Parts[partIndex].GetKey(localIndex, blockPin);
    return key;
  }

  public TValue GetValue(long index, BlockPin blockPin)
  {
    long off = 0;
    var partIndex = 0;
    var len = Parts[partIndex].Length;
    while (off + len <= index)
    {
      off += len;
      ++partIndex;
      len = Parts[partIndex].Length;
    }
    var localIndex = index - off;

    if (localIndex == 0)
      return PartValues[partIndex * 2];
    if (localIndex == len - 1)
      return PartValues[partIndex * 2 + 1];

    return Parts[partIndex].GetValue(localIndex, blockPin);
  }

  public int ReadEntries(
     long startIndex,
     int count,
     TKey[] keys,
     TValue[] values,
     int destinationIndex,
     BlockPin blockPin)
  {
    if (startIndex < 0 || startIndex >= Length || count <= 0)
      return 0;
    count = (int)Math.Min(count, Length - startIndex);

    var partIndex = 0;
    long partStartIndex = 0;
    var partLength = Parts[partIndex].Length;
    while (partStartIndex + partLength <= startIndex)
    {
      partStartIndex += partLength;
      ++partIndex;
      partLength = Parts[partIndex].Length;
    }

    var read = 0;
    while (read < count)
    {
      var part = Parts[partIndex];
      var localIndex = startIndex + read - partStartIndex;
      var partReadCount = (int)Math.Min(count - read, part.Length - localIndex);
      var targetIndex = destinationIndex + read;
      if (part is DiskSegment<TKey, TValue> diskSegment)
      {
        read += diskSegment.ReadEntries(
            localIndex,
            partReadCount,
            keys,
            values,
            targetIndex,
            blockPin);
      }
      else if (part is MultiPartDiskSegment<TKey, TValue> multiPartDiskSegment)
      {
        read += multiPartDiskSegment.ReadEntries(
            localIndex,
            partReadCount,
            keys,
            values,
            targetIndex,
            blockPin);
      }
      else
      {
        for (var i = 0; i < partReadCount; ++i)
        {
          keys[targetIndex + i] = part.GetKey(localIndex + i, blockPin);
          values[targetIndex + i] = part.GetValue(localIndex + i, blockPin);
        }
        read += partReadCount;
      }

      if (read == count)
        break;

      partStartIndex += part.Length;
      ++partIndex;
    }

    return read;
  }
}
