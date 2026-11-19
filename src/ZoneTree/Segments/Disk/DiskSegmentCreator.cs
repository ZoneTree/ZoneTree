using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ZoneTree.Core;
using ZoneTree.Options;
using ZoneTree.Segments.Model;
using ZoneTree.Segments.RandomAccess;
using ZoneTree.Serializers;

namespace ZoneTree.Segments.Disk;

public sealed class DiskSegmentCreator<TKey, TValue> : IDiskSegmentCreator<TKey, TValue>
{
  readonly long SegmentId;

  readonly ISerializer<TKey> KeySerializer;

  readonly ISerializer<TValue> ValueSerializer;

  // Keys and values are serialized into this buffer, one at a time, and their devices copy them in. A creator
  // writes one segment on one thread, so the buffer keeps any length: a segment of long values reuses one buffer
  // instead of allocating one for each value.
  readonly SerializationBuffer Buffer = new(int.MaxValue);

  IRandomAccessDevice DataHeaderDevice;

  IRandomAccessDevice DataDevice;

  readonly ZoneTreeOptions<TKey, TValue> Options;

  readonly bool HasFixedSizeKey;

  readonly bool HasFixedSizeValue;

  readonly bool HasFixedSizeKeyAndValue;

  public int Length { get; private set; }

  public bool CanSkipCurrentPart => false;

  public HashSet<long> AppendedPartSegmentIds { get; } = new();

  List<SparseArrayEntry<TKey, TValue>> DefaultSparseArray = new();

  int DefaultSparseArrayStepSize;

  TKey LastKey;

  TValue LastValue;

  bool IsReadOnlyDiskSegmentCreated;

  bool IsDropped;

  bool IsDisposed;

  public DiskSegmentCreator(
      ZoneTreeOptions<TKey, TValue> options,
      IIncrementalIdProvider incrementalIdProvider
      )
  {
    SegmentId = incrementalIdProvider.NextId();
    KeySerializer = options.KeySerializer;
    ValueSerializer = options.ValueSerializer;
    Options = options;
    var randomDeviceManager = options.RandomAccessDeviceManager;

    HasFixedSizeKey = !RuntimeHelpers.IsReferenceOrContainsReferences<TKey>();
    HasFixedSizeValue = !RuntimeHelpers.IsReferenceOrContainsReferences<TValue>();
    HasFixedSizeKeyAndValue = HasFixedSizeKey && HasFixedSizeValue;
    var diskOptions = options.DiskSegmentOptions;
    try
    {
      if (!HasFixedSizeKeyAndValue)
        DataHeaderDevice = randomDeviceManager
            .CreateWritableDevice(
                SegmentId,
                DiskSegmentConstants.DataHeaderCategory,
                isCompressed: true,
                diskOptions.CompressionBlockSize,
                deleteIfExists: true,
                backupIfDelete: false,
                diskOptions.CompressionMethod,
                diskOptions.CompressionLevel);
      DataDevice = randomDeviceManager
          .CreateWritableDevice(
              SegmentId,
              DiskSegmentConstants.DataCategory,
              isCompressed: true,
              diskOptions.CompressionBlockSize,
              deleteIfExists: true,
              backupIfDelete: false,
              diskOptions.CompressionMethod,
              diskOptions.CompressionLevel);
    }
    catch
    {
      Dispose();
      throw;
    }
    DefaultSparseArrayStepSize = options.DiskSegmentOptions.DefaultSparseArrayStepSize;
  }

  public void Append(TKey key, TValue value, IteratorPosition iteratorPosition)
  {
    LastKey = key;
    LastValue = value;
    if (DefaultSparseArrayStepSize > 0 && Length % DefaultSparseArrayStepSize == 0)
    {
      DefaultSparseArray.Add(new(key, value, Length));
    }
    ++Length;
    if (HasFixedSizeKeyAndValue)
    {
      Append(DataDevice, KeySerializer, key, out _);
      Append(DataDevice, ValueSerializer, value, out _);
      return;
    }

    if (HasFixedSizeKey)
    {
      var valueOffset = Append(DataDevice, ValueSerializer, value, out var valueLength);
      Append(DataHeaderDevice, KeySerializer, key, out _);
      AppendHead(DataHeaderDevice, new ValueHead
      {
        ValueLength = valueLength,
        ValueOffset = valueOffset
      });
      return;
    }

    if (HasFixedSizeValue)
    {
      var keyOffset = Append(DataDevice, KeySerializer, key, out var keyLength);
      AppendHead(DataHeaderDevice, new KeyHead
      {
        KeyLength = keyLength,
        KeyOffset = keyOffset,
      });
      Append(DataHeaderDevice, ValueSerializer, value, out _);
      return;
    }

    var off1 = Append(DataDevice, KeySerializer, key, out var len1);
    var off2 = Append(DataDevice, ValueSerializer, value, out var len2);
    AppendHead(DataHeaderDevice, new EntryHead
    {
      KeyLength = len1,
      ValueLength = len2,
      KeyOffset = off1,
      ValueOffset = off2
    });
  }

  // Serializes the entry into the buffer and appends its bytes. Returns their position.
  long Append<T>(
      IRandomAccessDevice device,
      ISerializer<T> serializer,
      in T entry,
      out int length)
  {
    var bytes = Buffer.Get(serializer.GetMaxByteCount(in entry));
    length = serializer.Serialize(in entry, bytes);
    return device.AppendBytesReturnPosition(bytes[..length]);
  }

  // Appends the head's bytes from the head itself. A head made with new has zero padding, so the bytes are those
  // BinarySerializerHelper.ToByteArray returns.
  static void AppendHead<THead>(IRandomAccessDevice device, in THead head) where THead : unmanaged
  {
    device.AppendBytesReturnPosition(
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in head), 1)));
  }

  public IDiskSegment<TKey, TValue> CreateReadOnlyDiskSegment()
  {
    ThrowIfNotWritable();

    var isSparseArrayNotEmpty = DefaultSparseArray.Count > 0;
    if (isSparseArrayNotEmpty &&
        DefaultSparseArrayStepSize > 0 &&
        DefaultSparseArray.Last().Index + 1 != Length)
    {
      DefaultSparseArray.Add(new(LastKey, LastValue, Length - 1));
    }
    DataHeaderDevice?.SealDevice();
    DataDevice?.SealDevice();
    DataHeaderDevice?.ReleaseInactiveCachedBuffers(0);
    DataDevice?.ReleaseInactiveCachedBuffers(0);
    var diskSegment = DiskSegmentFactory.CreateDiskSegment<TKey, TValue>(
        SegmentId, Options,
        DataHeaderDevice, DataDevice);
    if (isSparseArrayNotEmpty)
      diskSegment.SetDefaultSparseArray(DefaultSparseArray);
    DataHeaderDevice = null;
    DataDevice = null;
    IsReadOnlyDiskSegmentCreated = true;
    return diskSegment;
  }

  public void DropDiskSegment()
  {
    if (IsDropped)
      return;
    if (IsReadOnlyDiskSegmentCreated)
      throw new InvalidOperationException(
          "Cannot drop a disk segment creator after ownership has been transferred.");
    ObjectDisposedException.ThrowIf(
        IsDisposed,
        this);

    Close();
    if (!HasFixedSizeKeyAndValue)
    {
      DataHeaderDevice?.Delete();
      DataHeaderDevice = null;
    }
    DataDevice?.Delete();
    DataDevice = null;
    IsDropped = true;
  }

  void Close()
  {
    if (!HasFixedSizeKeyAndValue)
      DataHeaderDevice?.Close();
    DataDevice?.Close();
  }

  public void Dispose()
  {
    if (IsReadOnlyDiskSegmentCreated || IsDropped || IsDisposed)
      return;

    if (!HasFixedSizeKeyAndValue)
      DataHeaderDevice?.Dispose();
    DataDevice?.Dispose();
    DataHeaderDevice = null;
    DataDevice = null;
    IsDisposed = true;
  }

  public void Append(IDiskSegment<TKey, TValue> part, TKey key1, TKey key2, TValue value1, TValue value2)
  {
    throw new NotSupportedException("This method should be called on MultiDiskSegmentCreator.");
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
