using ZoneTree.AbstractFileStream;
using ZoneTree.Core;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Segments.RandomAccess;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

/// <summary>
/// Reads the records that a closed tree's shared journal holds for its live segments, as the
/// bytes its serializers wrote, without the tree's key and value types.
/// </summary>
/// <remarks>
/// <para>
/// It lets one tree's recovery depend on the records of another tree that is not open. The
/// tree's metadata is read read-only. Opening the journal runs the journal's own recovery,
/// which repairs only what the tree's own open would repair, such as an unsynced torn tail, so
/// the tree later recovers exactly the records read here.
/// </para>
/// <para>
/// It reads trees in the default layout: a shared journal in the tree's data directory, with
/// the default device manager. The tree must not be open.
/// </para>
/// </remarks>
internal static class SharedJournalReader
{
  /// <summary>A record of a live segment: its serialized key and value.</summary>
  internal readonly record struct Record(Memory<byte> Key, Memory<byte> Value);

  /// <summary>
  /// Reads the records of the tree's live segments: the read-only segments from the oldest,
  /// then the mutable segment, each in write order. Returns no records if the tree has no
  /// metadata or no shared journal.
  /// </summary>
  internal static List<Record> ReadLiveRecords(
      ILogger logger, IFileStreamProvider files, string dataDirectory, WriteAheadLogOptions walOptions)
  {
    var records = new List<Record>();
    if (!files.DirectoryExists(dataDirectory))
      return records;
    var journalDirectory = Path.Combine(
        dataDirectory, ZoneTree<byte[], byte[]>.SegmentWalCategory, SharedWalStore<byte[], byte[]>.DirectoryName);
    if (!files.FileExists(Path.Combine(journalDirectory, SharedWalStore<byte[], byte[]>.ManifestName)))
      return records;
    var liveSegments = ReadLiveSegments(logger, files, dataDirectory);
    if (liveSegments.Count == 0)
      return records;
    var serializer = new BytesSerializer();
    var store = new SharedWalStore<byte[], byte[]>(logger, files, journalDirectory, walOptions, serializer, serializer);
    try
    {
      foreach (var segment in liveSegments)
      {
        foreach (var record in store.Read(segment, out _))
          if (record.Kind == SharedWalRecordKind.Data)
            records.Add(new Record(record.Key, record.Value));
      }
    }
    finally
    {
      store.Close();
    }
    return records;
  }

  /// <summary>
  /// The live segments of the tree, the read-only ones from the oldest, then the mutable one.
  /// The metadata and its log are only read.
  /// </summary>
  static List<long> ReadLiveSegments(ILogger logger, IFileStreamProvider files, string dataDirectory)
  {
    var deviceManager = new RandomAccessDeviceManager(logger, files, dataDirectory);
    try
    {
      var options = new ZoneTreeOptions<byte[], byte[]>
      {
        Logger = logger,
        RandomAccessDeviceManager = deviceManager
      };
      if (!ZoneTreeMetaWAL<byte[], byte[]>.Exists(options))
        return [];
      var meta = ZoneTreeMetaWAL<byte[], byte[]>.LoadZoneTreeMetaWithoutWALRecords(deviceManager);
      var mutableSegment = meta.MutableSegment;
      // The newest read-only segment comes first, as the tree loads them.
      var readOnlySegments = meta.ReadOnlySegments?.ToList() ?? [];
      using (var metaWal = new ZoneTreeMetaWAL<byte[], byte[]>(options, isReadOnly: true))
      {
        foreach (var record in metaWal.GetRecordsOfGeneration(meta.MetaWalGeneration))
        {
          switch (record.Operation)
          {
            case MetaWalOperation.NewMutableSegment:
              mutableSegment = record.SegmentId;
              break;
            case MetaWalOperation.EnqueueReadOnlySegment:
              readOnlySegments.Insert(0, record.SegmentId);
              break;
            case MetaWalOperation.DequeueReadOnlySegment:
              if (readOnlySegments.Count != 0 && readOnlySegments[^1] == record.SegmentId)
                readOnlySegments.RemoveAt(readOnlySegments.Count - 1);
              break;
          }
        }
      }
      var live = new List<long>(readOnlySegments.Count + 1);
      for (var index = readOnlySegments.Count - 1; index >= 0; --index)
        live.Add(readOnlySegments[index]);
      // A segment that became read-only still has its records under its own id.
      if (mutableSegment > 0 && !live.Contains(mutableSegment))
        live.Add(mutableSegment);
      return live;
    }
    finally
    {
      deviceManager.CloseAllDevices();
    }
  }
}
