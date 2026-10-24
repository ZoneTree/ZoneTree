using System.Runtime.CompilerServices;
using ZoneTree.AbstractFileStream;
using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Segments.Block;
using ZoneTree.Segments.Disk;
using ZoneTree.Segments.RandomAccess;
using ZoneTree.Serializers;

namespace ZoneTree.Segments.DiskSegmentVariations;

public sealed class FixedSizeKeyAndValueDiskSegment<TKey, TValue> : DiskSegment<TKey, TValue>
{
  public override int ReadBufferCount =>
      (DataDevice?.ReadBufferCount ?? 0);

  public FixedSizeKeyAndValueDiskSegment(
      long segmentId,
      ZoneTreeOptions<TKey, TValue> options) : base(segmentId, options)
  {
    EnsureKeyAndValueTypesAreSupported();
    var randomDeviceManager = options.RandomAccessDeviceManager;
    var diskOptions = options.DiskSegmentOptions;
    DataDevice = randomDeviceManager
        .GetReadOnlyDevice(
            SegmentId,
            DiskSegmentConstants.DataCategory,
            isCompressed: true,
            diskOptions.CompressionBlockSize,
            diskOptions.CompressionMethod,
            diskOptions.CompressionLevel);
    InitKeyAndValueSizeAndDataLength();
    LoadDefaultSparseArray();
  }

  public FixedSizeKeyAndValueDiskSegment(long segmentId,
      ZoneTreeOptions<TKey, TValue> options,
      IRandomAccessDevice dataDevice) : base(segmentId, options, dataDevice)
  {
    EnsureKeyAndValueTypesAreSupported();
    InitKeyAndValueSizeAndDataLength();
    LoadDefaultSparseArray();
  }

  static void EnsureKeyAndValueTypesAreSupported()
  {
    var hasFixedSizeKey = !RuntimeHelpers.IsReferenceOrContainsReferences<TKey>();
    var hasFixedSizeValue = !RuntimeHelpers.IsReferenceOrContainsReferences<TValue>();
    if (!hasFixedSizeKey || !hasFixedSizeValue)
    {
      throw new Exception("The FixedSizeKeyDiskSegment requires fixed size key and fixed size value.");
    }
  }

  void InitKeyAndValueSizeAndDataLength()
  {
    KeySize = Unsafe.SizeOf<TKey>();
    ValueSize = Unsafe.SizeOf<TValue>();
    Length = DataDevice.Length / (KeySize + ValueSize);
  }

  void LoadDefaultSparseArray()
  {
    var options = Options;
    var diskOptions = options.DiskSegmentOptions;
    if (diskOptions.DefaultSparseArrayStepSize == 0) return;
    var deviceManager = options.RandomAccessDeviceManager;
    if (!deviceManager.DeviceExists(
        SegmentId,
        DiskSegmentConstants.SparseArrayCategory,
        isCompressed: true))
      return;
    using var sparseArrayDevice = deviceManager.GetReadOnlyDevice(
        SegmentId,
        DiskSegmentConstants.SparseArrayCategory,
        isCompressed: true,
        diskOptions.CompressionBlockSize,
        diskOptions.CompressionMethod,
        diskOptions.CompressionLevel);
    var recordCount = BinarySerializerHelper.FromByteArray<int>(sparseArrayDevice.GetBytes(0, sizeof(int)));
    var offset = sizeof(int);
    var keySize = KeySize;
    var valueSize = ValueSize;
    var sparseArray = new SparseArrayEntry<TKey, TValue>[recordCount];
    for (var i = 0; i < recordCount; ++i)
    {
      var key = KeySerializer.Deserialize(sparseArrayDevice.GetBytes(offset, keySize));
      offset += keySize;
      var value = ValueSerializer.Deserialize(sparseArrayDevice.GetBytes(offset, valueSize));
      offset += valueSize;
      var index = BinarySerializerHelper.FromByteArray<long>(sparseArrayDevice.GetBytes(offset, sizeof(long)));
      offset += sizeof(long);
      var entry = new SparseArrayEntry<TKey, TValue>(key, value, index);
      sparseArray[i] = entry;
    }
    SparseArray = sparseArray;
    sparseArrayDevice.Close();
  }

  public override void SetDefaultSparseArray(IReadOnlyList<SparseArrayEntry<TKey, TValue>> defaultSparseArray)
  {
    SparseArray = defaultSparseArray;
    var options = Options;
    var diskOptions = options.DiskSegmentOptions;
    var deviceManager = options.RandomAccessDeviceManager;
    using var sparseArrayDevice = deviceManager.CreateWritableDevice(
        SegmentId,
        DiskSegmentConstants.SparseArrayCategory,
        isCompressed: true,
        diskOptions.CompressionBlockSize,
        true,
        false,
        diskOptions.CompressionMethod,
        diskOptions.CompressionLevel);
    var sparseArray = SparseArray;
    var recordCount = sparseArray.Count;
    sparseArrayDevice.AppendBytesReturnPosition(BitConverter.GetBytes(recordCount));
    for (var i = 0; i < recordCount; ++i)
    {
      var entry = sparseArray[i];
      sparseArrayDevice.AppendBytesReturnPosition(KeySerializer.Serialize(entry.Key));
      sparseArrayDevice.AppendBytesReturnPosition(ValueSerializer.Serialize(entry.Value));
      sparseArrayDevice.AppendBytesReturnPosition(BitConverter.GetBytes(entry.Index));
    }
    sparseArrayDevice.SealDevice();
    sparseArrayDevice.Close();
  }

  protected override long SearchKeys(in TKey key, long lower, long upper)
      => SearchFixedSizeKeys(in key, lower, upper, DataDevice, KeySize + ValueSize);

  protected override TKey ReadKey(long index, BlockPin blockPin)
  {
    if (CircularKeyCache.TryGet(index, out var key)) return key;
    var readStripe = BeginRead();
    try
    {
      var itemSize = KeySize + ValueSize;
      var pin1 = blockPin?.ToSingleBlockPin(1);
      var keyBytes = DataDevice.GetBytes(
          itemSize * index,
          KeySize,
          pin1);
      blockPin?.SetDevice1(pin1.Device);
      key = KeySerializer.Deserialize(keyBytes);
      CircularKeyCache.TryAdd(index, ref key);
      return key;
    }
    finally
    {
      EndRead(readStripe);
    }
  }

  protected override TValue ReadValue(long index, BlockPin blockPin)
  {
    if (CircularValueCache.TryGet(index, out var value)) return value;
    var readStripe = BeginRead();
    try
    {
      var itemSize = KeySize + ValueSize;
      var pin1 = blockPin?.ToSingleBlockPin(1);
      var valueBytes = DataDevice.GetBytes(
          itemSize * index + KeySize,
          ValueSize,
          pin1);
      blockPin?.SetDevice1(pin1.Device);
      value = ValueSerializer.Deserialize(valueBytes);
      CircularValueCache.TryAdd(index, ref value);
      return value;
    }
    finally
    {
      EndRead(readStripe);
    }
  }

  public override int ReadEntries(
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

    var readStripe = BeginRead();
    try
    {
      var itemSize = KeySize + ValueSize;
      var pin1 = blockPin?.ToSingleBlockPin(1);
      var bytes = DataDevice.GetBytes(
          itemSize * startIndex,
          itemSize * count,
          pin1);
      MaterializedEntryCache<TKey, TValue> cache = null;

      if (count > 1)
      {
        cache = MaterializedEntryCache<TKey, TValue>.GetOrCreate(
            pin1?.Device,
            MaterializedEntryCacheSize);
        if (cache != null && cache.TryCopy(
            startIndex,
            count,
            keys,
            values,
            destinationIndex))
        {
          blockPin?.SetDevice1(pin1.Device);
          return count;
        }
      }

      for (var i = 0; i < count; ++i)
      {
        var sourceOffset = i * itemSize;
        var targetIndex = destinationIndex + i;
        keys[targetIndex] = KeySerializer.Deserialize(
            bytes.Slice(sourceOffset, KeySize));
        values[targetIndex] = ValueSerializer.Deserialize(
            bytes.Slice(sourceOffset + KeySize, ValueSize));
      }

      cache?.Add(startIndex, count, keys, values, destinationIndex);
      blockPin?.SetDevice1(pin1.Device);
      return count;
    }
    finally
    {
      EndRead(readStripe);
    }
  }

  protected override void DeleteDevices()
  {
    DataDevice?.Delete();
    Options.RandomAccessDeviceManager.DeleteDevice(SegmentId,
        DiskSegmentConstants.SparseArrayCategory,
        isCompressed: true);
  }

  public override void ReleaseResources()
  {
    base.ReleaseResources();
  }

  public override int ReleaseReadBuffers(long ticks)
  {
    return DataDevice?.ReleaseInactiveCachedBuffers(ticks) ?? 0;
  }
}
