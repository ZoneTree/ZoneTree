using System.Buffers.Binary;
using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

/// <summary>
/// A crash while appending a record leaves an incomplete record at the end of
/// a WAL. Opening the tree truncates it. These tests check that the truncation
/// keeps every complete record and that the WAL stays writable afterwards.
/// </summary>
public sealed class WalTruncationRecoveryTests
{
  const int WalBlockSize = 64;

  [Test]
  public void RecoveryKeepsRecordsBeforeIncompleteRecordCrossingBlockBoundary()
  {
    var dataPath = "data/RecoveryKeepsRecordsBeforeIncompleteRecordCrossingBlockBoundary";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    var keys = Enumerable.Range(1, 10).ToArray();

    long segmentId;
    using (var zoneTree = OpenOrCreate(dataPath))
    {
      foreach (var key in keys)
        zoneTree.Upsert(key, key);
      zoneTree.Maintenance.MoveMutableSegmentForward();
      segmentId = zoneTree.Maintenance.ReadOnlySegments[0].SegmentId;
    }

    // The incomplete record is longer than a block,
    // so it starts in a committed block and continues in the tail.
    AppendIncompleteRecord(dataPath, segmentId, WalBlockSize + 8);

    // The first open truncates the incomplete record.
    using (OpenOrCreate(dataPath))
    {
    }

    using (var zoneTree = OpenOrCreate(dataPath))
    {
      var missingKeys = keys.Where(key => !zoneTree.ContainsKey(key)).ToArray();
      Assert.That(missingKeys, Is.Empty);
    }
  }

  [Test]
  public void RecoveredMutableSegmentKeepsNewWrites()
  {
    var dataPath = "data/RecoveredMutableSegmentKeepsNewWrites";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    long segmentId;
    using (var zoneTree = OpenOrCreate(dataPath))
    {
      zoneTree.Upsert(1, 1);
      segmentId = zoneTree.Maintenance.MutableSegment.SegmentId;
    }

    // An int/int record is 28 bytes. The incomplete record stays in the same
    // tail block as the complete record, so the truncation stays within the tail.
    AppendIncompleteRecord(dataPath, segmentId, 28);

    using (var zoneTree = OpenOrCreate(dataPath))
    {
      zoneTree.Upsert(2, 2);
      zoneTree.Upsert(3, 3);
    }

    using (var zoneTree = OpenOrCreate(dataPath))
    {
      var missingKeys = new[] { 1, 2, 3 }.Where(key => !zoneTree.ContainsKey(key)).ToArray();
      Assert.That(missingKeys, Is.Empty);
    }
  }

  [Test]
  public void RecoveredMutableSegmentKeepsRecordsAndNewWritesAfterTruncatingIntoMainFile()
  {
    var dataPath = "data/RecoveredMutableSegmentKeepsRecordsAndNewWritesAfterTruncatingIntoMainFile";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    var keys = Enumerable.Range(1, 10).ToArray();
    var newKeys = Enumerable.Range(11, 20).ToArray();

    long segmentId;
    using (var zoneTree = OpenOrCreate(dataPath))
    {
      foreach (var key in keys)
        zoneTree.Upsert(key, key);
      segmentId = zoneTree.Maintenance.MutableSegment.SegmentId;
    }

    // The incomplete record is longer than a block,
    // so it starts in a committed block and continues in the tail.
    AppendIncompleteRecord(dataPath, segmentId, WalBlockSize + 8);

    // Enough new records to commit several blocks after the truncation.
    using (var zoneTree = OpenOrCreate(dataPath))
    {
      foreach (var key in newKeys)
        zoneTree.Upsert(key, key);
    }

    using (var zoneTree = OpenOrCreate(dataPath))
    {
      var missingKeys = keys.Concat(newKeys).Where(key => !zoneTree.ContainsKey(key)).ToArray();
      Assert.That(missingKeys, Is.Empty);
    }
  }

  static IZoneTree<int, int> OpenOrCreate(string dataPath)
  {
    return new ZoneTreeFactory<int, int>()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetDataDirectory(dataPath)
        .ConfigureWriteAheadLogOptions(options => options.CompressionBlockSize = WalBlockSize)
        .OpenOrCreate();
  }

  /// <summary>
  /// Appends the start of a record whose declared value length exceeds
  /// the appended bytes, as a crash in the middle of an append leaves it.
  /// </summary>
  static void AppendIncompleteRecord(string dataPath, long segmentId, int length)
  {
    var walPath = Directory
        .GetFiles(Path.Combine(dataPath, "seg"), $"{segmentId}.wal_*")
        .Single(path => !path.EndsWith(".tail", StringComparison.Ordinal));
    var bytes = new byte[length];
    // BinaryWriter writes WAL records in little-endian order.
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0, sizeof(long)), 1_000_000L);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, sizeof(int)), sizeof(int));
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12, sizeof(int)), 100_000);

    // The compression method of an existing file is read from the file.
    using var stream = new CompressedFileStream(
        new ConsoleLogger(),
        new LocalFileStreamProvider(),
        walPath,
        WalBlockSize,
        false,
        0,
        CompressionMethod.None,
        0);
    stream.Seek(0, SeekOrigin.End);
    stream.Write(bytes, 0, bytes.Length);
  }
}
