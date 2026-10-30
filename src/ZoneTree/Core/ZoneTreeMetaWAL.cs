using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZoneTree.Exceptions;
using ZoneTree.Options;
using ZoneTree.Segments.RandomAccess;
using ZoneTree.Serializers;

namespace ZoneTree.Core;

public sealed class ZoneTreeMetaWAL<TKey, TValue> : IDisposable
{
  readonly Lock SyncRoot = new();

  const CompressionMethod MetaWALCompressionMethod = CompressionMethod.None;

  const int MetaWALCompressionLevel = 0;

  const int ZoneTreeMetaId = 0;

  const string MetaWalCategory = ".meta.wal";

  const string MetaFileCategory = ".json";

  IRandomAccessDevice Device { get; set; }

  public ZoneTreeOptions<TKey, TValue> Options { get; }

  public bool IsReadOnly { get; }

  // The generation of the records that this WAL appends.
  long Generation;

  public ZoneTreeMetaWAL(ZoneTreeOptions<TKey, TValue> options, bool isReadOnly, long generation = 0)
  {
    Options = options;
    IsReadOnly = isReadOnly;
    Generation = generation;
    if (IsReadOnly)
    {
      Device = Options
          .RandomAccessDeviceManager
          .GetReadOnlyDevice(
              ZoneTreeMetaId,
              MetaWalCategory,
              isCompressed: false,
              compressionBlockSize: 0,
              MetaWALCompressionMethod,
              MetaWALCompressionLevel);
    }
    else
    {

      Device = Options
          .RandomAccessDeviceManager
          .CreateWritableDevice(
              ZoneTreeMetaId,
              MetaWalCategory,
              isCompressed: false,
              compressionBlockSize: 0,
              deleteIfExists: false,
              backupIfDelete: false,
              MetaWALCompressionMethod,
              MetaWALCompressionLevel);
    }
  }

  public static bool Exists(ZoneTreeOptions<TKey, TValue> options)
  {
    return options
        .RandomAccessDeviceManager
        .DeviceExists(
            ZoneTreeMetaId,
            MetaFileCategory, false);

  }

  public void EnqueueMaximumOpIndex(long maximumOpIndex)
  {
    var record = new MetaWalRecord
    {
      Operation = MetaWalOperation.EnqueueMaximumOpIndex,
      SegmentId = maximumOpIndex
    };
    AppendRecord(record);
  }

  public void EnqueueReadOnlySegment(long segmentId)
  {
    var record = new MetaWalRecord
    {
      Operation = MetaWalOperation.EnqueueReadOnlySegment,
      SegmentId = segmentId
    };
    AppendRecord(record);
  }

  public void DequeueReadOnlySegment(long segmentId)
  {
    var record = new MetaWalRecord
    {
      Operation = MetaWalOperation.DequeueReadOnlySegment,
      SegmentId = segmentId
    };
    AppendRecord(record);
  }

  public void NewMutableSegment(long segmentId)
  {
    var record = new MetaWalRecord
    {
      Operation = MetaWalOperation.NewMutableSegment,
      SegmentId = segmentId
    };
    AppendRecord(record);
  }

  public void NewDiskSegment(long segmentId)
  {
    var record = new MetaWalRecord
    {
      Operation = MetaWalOperation.NewDiskSegment,
      SegmentId = segmentId
    };
    AppendRecord(record);
  }

  public void EnqueueBottomSegment(long segmentId)
  {
    var record = new MetaWalRecord
    {
      Operation = MetaWalOperation.EnqueueBottomSegment,
      SegmentId = segmentId
    };
    AppendRecord(record);
  }

  public void DequeueBottomSegment(long segmentId)
  {
    var record = new MetaWalRecord
    {
      Operation = MetaWalOperation.DequeueBottomSegment,
      SegmentId = segmentId
    };
    AppendRecord(record);
  }

  public void InsertBottomSegment(long segmentId, int index)
  {
    var record = new MetaWalRecord
    {
      Operation = MetaWalOperation.InsertBottomSegment,
      SegmentId = segmentId,
      Index = index
    };
    AppendRecord(record);
  }

  public void DeleteBottomSegment(long segmentId)
  {
    var record = new MetaWalRecord
    {
      Operation = MetaWalOperation.DeleteBottomSegment,
      SegmentId = segmentId
    };
    AppendRecord(record);
  }

  public void ClearContent()
  {
    Device.ClearContent();
  }

  public void DeleteFile()
  {
    Device.Delete();
  }

  public void Dispose()
  {
    if (IsReadOnly)
    {
      Options
          .RandomAccessDeviceManager
          .RemoveReadOnlyDevice(ZoneTreeMetaId, MetaWalCategory);
    }
    else
    {
      Options
          .RandomAccessDeviceManager
          .RemoveWritableDevice(ZoneTreeMetaId, MetaWalCategory);
    }
    Device?.Dispose();
    Device = null;
  }

  void AppendRecord(in MetaWalRecord record)
  {
    lock (SyncRoot)
    {
      var stamped = record;
      stamped.Generation = unchecked((uint)Generation);
      stamped.Checksum = stamped.CreateChecksum();
      var bytes = BinarySerializerHelper.ToByteArray(stamped);
      Device.AppendBytesReturnPosition(bytes);
    }
  }

  public unsafe IReadOnlyList<MetaWalRecord> GetAllRecords()
  {
    var len = Device.Length;
    if (len > int.MaxValue)
      throw new DataIsTooBigToLoadAtOnceException(len, int.MaxValue);
    var bytes = Device.GetBytes(0, (int)len);
    var list = new List<MetaWalRecord>();
    var off = 0;
    // A power loss can leave a partial record at the end of the file.
    while (off + sizeof(MetaWalRecord) <= len)
    {
      var record = BinarySerializerHelper.FromByteArray<MetaWalRecord>(bytes, off);
      list.Add(record);
      off += sizeof(MetaWalRecord);
    }
    return list;
  }

  /// <summary>
  /// Returns the records that the metadata of the given generation does not include.
  /// </summary>
  /// <remarks>
  /// A power loss can leave records of an older generation, which the metadata
  /// includes, and an invalid last record, whose append was not synced.
  /// </remarks>
  /// <exception cref="ZoneTreeMetaCorruptionException">
  /// A record of this generation follows an invalid record.
  /// Each append is synced, so a power loss cannot explain it.
  /// </exception>
  public IReadOnlyList<MetaWalRecord> GetRecordsOfGeneration(long generation)
  {
    var records = GetAllRecords();
    // An older version saved the metadata and its records have no generation.
    if (generation == 0)
      return records;
    var list = new List<MetaWalRecord>(records.Count);
    var foundInvalidRecord = false;
    foreach (var record in records)
    {
      if (record.Checksum != record.CreateChecksum())
      {
        foundInvalidRecord = true;
        continue;
      }
      if (record.Generation != unchecked((uint)generation))
        continue;
      if (foundInvalidRecord)
        throw new ZoneTreeMetaCorruptionException();
      list.Add(record);
    }
    return list;
  }

  public void SaveMetaData(
      ZoneTreeMeta zoneTreeMeta,
      long mutableSegment,
      long diskSegment,
      long[] readOnlySegments,
      long[] bottomSegments,
      bool createNew = false)
  {
    string productVersion = ZoneTreeInfo.ProductVersion.ToString();
    var newZoneTreeMeta = new ZoneTreeMeta
    {
      Version = productVersion,
      KeyType = zoneTreeMeta.KeyType,
      ValueType = zoneTreeMeta.ValueType,
      ComparerType = zoneTreeMeta.ComparerType,
      KeyHasherType = Options.KeyHasher?.GetType().SimplifiedFullName(),
      DiskSegment = diskSegment,
      ReadOnlySegments = readOnlySegments,
      MutableSegment = mutableSegment,
      KeySerializerType = zoneTreeMeta.KeySerializerType,
      ValueSerializerType = zoneTreeMeta.ValueSerializerType,
      WriteAheadLogOptions = Options.WriteAheadLogOptions,
      DiskSegmentOptions = Options.DiskSegmentOptions,
      MutableSegmentMaxItemCount = Options.MutableSegmentMaxItemCount,
      MutableSegmentBloomFilterBitsPerItem =
          Options.MutableSegmentBloomFilterBitsPerItem,
      DiskSegmentMaxItemCount = Options.DiskSegmentMaxItemCount,
      BottomSegments = bottomSegments,
      MaximumOpIndex = zoneTreeMeta.MaximumOpIndex,
      MetaWalGeneration = zoneTreeMeta.MetaWalGeneration + 1,
    };

    var bytes = JsonSerializeToUtf8Bytes(newZoneTreeMeta);
    var deviceManager = Options.RandomAccessDeviceManager;
    var metaFilePath = deviceManager.GetFilePath(0, MetaFileCategory);

    if (createNew)
    {
      deviceManager
          .FileStreamProvider
          .GetDurableFileWriter()
          .WriteNewFile(metaFilePath, bytes);
    }
    else
    {
      deviceManager
          .FileStreamProvider
          .GetDurableFileWriter()
          .WriteAllBytes(metaFilePath, bytes);
    }
    // The saved metadata makes the existing records stale, even if clearing
    // them fails or does not reach the disk. So it does not need a disk sync.
    lock (SyncRoot)
      Generation = newZoneTreeMeta.MetaWalGeneration;
    zoneTreeMeta.MetaWalGeneration = newZoneTreeMeta.MetaWalGeneration;
    ClearContent();
    zoneTreeMeta.Version = productVersion;
    zoneTreeMeta.MutableSegment = mutableSegment;
    zoneTreeMeta.DiskSegment = diskSegment;
    zoneTreeMeta.ReadOnlySegments = readOnlySegments;
    zoneTreeMeta.BottomSegments = bottomSegments;
    zoneTreeMeta.WriteAheadLogOptions = Options.WriteAheadLogOptions;
    zoneTreeMeta.DiskSegmentOptions = Options.DiskSegmentOptions;
    zoneTreeMeta.MutableSegmentMaxItemCount = Options.MutableSegmentMaxItemCount;
    zoneTreeMeta.KeyHasherType = Options.KeyHasher?.GetType().SimplifiedFullName();
    zoneTreeMeta.MutableSegmentBloomFilterBitsPerItem =
        Options.MutableSegmentBloomFilterBitsPerItem;
    zoneTreeMeta.DiskSegmentMaxItemCount = Options.DiskSegmentMaxItemCount;
  }

  public static ZoneTreeMeta LoadZoneTreeMetaWithoutWALRecords(
      IRandomAccessDeviceManager deviceManager)
  {
    using var device = deviceManager
        .GetReadOnlyDevice(
            ZoneTreeMetaId,
            MetaFileCategory,
            isCompressed: false,
            compressionBlockSize: 0,
            MetaWALCompressionMethod,
            MetaWALCompressionLevel);

    if (device.Length > int.MaxValue)
      throw new DataIsTooBigToLoadAtOnceException(device.Length, int.MaxValue);
    var bytes = device.GetBytes(0, (int)device.Length);
    device.Close();
    deviceManager.RemoveReadOnlyDevice(device.SegmentId, MetaFileCategory);
    var meta = JsonDeserialize(bytes.Span);
    return meta;
  }

  private static byte[] JsonSerializeToUtf8Bytes(ZoneTreeMeta meta)
  {
    return JsonSerializer.SerializeToUtf8Bytes(
        meta,
        ZoneTreeMetaSourceGenerationContext.Default.ZoneTreeMeta);
  }

  private static ZoneTreeMeta JsonDeserialize(byte[] bytes)
  {
    return JsonSerializer.Deserialize<ZoneTreeMeta>(bytes, ZoneTreeMetaSourceGenerationContext.Default.ZoneTreeMeta);
  }

  private static ZoneTreeMeta JsonDeserialize(Span<byte> bytes)
  {
    return JsonSerializer.Deserialize<ZoneTreeMeta>(bytes, ZoneTreeMetaSourceGenerationContext.Default.ZoneTreeMeta);
  }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ZoneTreeMeta))]
internal sealed partial class ZoneTreeMetaSourceGenerationContext : JsonSerializerContext
{
}

public enum MetaWalOperation
{
  EnqueueReadOnlySegment,
  DequeueReadOnlySegment,
  NewMutableSegment,
  NewDiskSegment,
  EnqueueBottomSegment,
  DequeueBottomSegment,
  InsertBottomSegment,
  DeleteBottomSegment,
  EnqueueMaximumOpIndex,
}

[StructLayout(LayoutKind.Sequential)]
public struct MetaWalRecord : IEquatable<MetaWalRecord>
{
  public MetaWalOperation Operation;

  // Generation and Checksum use the padding of the record layout of older
  // versions, so the record size stays the same and they can read these records.
  public uint Generation;

  public long SegmentId;

  public int Index;

  public uint Checksum;

  public readonly uint CreateChecksum()
  {
    Span<byte> bytes = stackalloc byte[2 * sizeof(int) + sizeof(long) + sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)Operation);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes[sizeof(int)..], Generation);
    BinaryPrimitives.WriteInt64LittleEndian(bytes[(2 * sizeof(int))..], SegmentId);
    BinaryPrimitives.WriteInt32LittleEndian(bytes[(2 * sizeof(int) + sizeof(long))..], Index);
    return unchecked((uint)XxHash3.HashToUInt64(bytes));
  }

  public override bool Equals(object obj)
  {
    return obj is MetaWalRecord record && Equals(record);
  }

  public bool Equals(MetaWalRecord other)
  {
    return Operation == other.Operation &&
           Generation == other.Generation &&
           SegmentId == other.SegmentId &&
           Index == other.Index &&
           Checksum == other.Checksum;
  }

  public override int GetHashCode()
  {
    return HashCode.Combine(Operation, Generation, SegmentId, Index, Checksum);
  }

  public static bool operator ==(MetaWalRecord left, MetaWalRecord right)
  {
    return left.Equals(right);
  }

  public static bool operator !=(MetaWalRecord left, MetaWalRecord right)
  {
    return !(left == right);
  }
}
