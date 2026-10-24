using System.Runtime.CompilerServices;
using ZoneTree.Core;
using ZoneTree.Serializers;

namespace ZoneTree.UnitTests;

/// <summary>
/// MoveMutableSegmentForward appends EnqueueReadOnlySegment and NewMutableSegment
/// as separate meta WAL records. A crash between them leaves only the first one.
/// </summary>
public sealed class MetaWalCrashRecoveryTests
{
  [Test]
  public void CrashBeforeNewMutableSegmentRecordKeepsTreeOpenable()
  {
    AssertRecoveredTreeKeepsAllKeys(
        "data/CrashBeforeNewMutableSegmentRecordKeepsTreeOpenable",
        zoneTree => zoneTree.Maintenance.MoveMutableSegmentForward());
  }

  [Test]
  public void CrashBeforeNewMutableSegmentRecordKeepsWritesAfterMerge()
  {
    // The merge creates a disk segment with a new id, so a later load
    // must reuse the persisted mutable segment id instead of choosing a new one.
    AssertRecoveredTreeKeepsAllKeys(
        "data/CrashBeforeNewMutableSegmentRecordKeepsWritesAfterMerge",
        zoneTree => zoneTree.Maintenance.StartMergeOperation().Join());
  }

  static void AssertRecoveredTreeKeepsAllKeys(
      string dataPath,
      Action<IZoneTree<int, int>> afterWrites)
  {
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using (var zoneTree = OpenOrCreate(dataPath))
    {
      for (var key = 1; key <= 100; ++key)
        zoneTree.Upsert(key, key);
      zoneTree.Maintenance.MoveMutableSegmentForward();
    }

    // Drop the last meta WAL record, NewMutableSegment,
    // as a crash right after EnqueueReadOnlySegment leaves it.
    var metaWalPath = Path.Combine(dataPath, "0.meta.wal");
    var recordSize = Unsafe.SizeOf<MetaWalRecord>();
    var metaWal = File.ReadAllBytes(metaWalPath);
    Assert.That(metaWal.Length % recordSize, Is.Zero);
    Assert.That(metaWal.Length, Is.GreaterThanOrEqualTo(2 * recordSize));
    var enqueueRecord = BinarySerializerHelper
        .FromByteArray<MetaWalRecord>(metaWal, metaWal.Length - 2 * recordSize);
    var newMutableSegmentRecord = BinarySerializerHelper
        .FromByteArray<MetaWalRecord>(metaWal, metaWal.Length - recordSize);
    Assert.That(enqueueRecord.Operation, Is.EqualTo(MetaWalOperation.EnqueueReadOnlySegment));
    Assert.That(newMutableSegmentRecord.Operation, Is.EqualTo(MetaWalOperation.NewMutableSegment));
    File.WriteAllBytes(metaWalPath, metaWal[..^recordSize]);

    using (var zoneTree = OpenOrCreate(dataPath))
    {
      for (var key = 101; key <= 200; ++key)
        zoneTree.Upsert(key, key);
      afterWrites(zoneTree);
    }

    using (var zoneTree = OpenOrCreate(dataPath))
    {
      var missingKeys = Enumerable.Range(1, 200).Where(key => !zoneTree.ContainsKey(key)).ToArray();
      Assert.That(missingKeys, Is.Empty);
    }
  }

  static IZoneTree<int, int> OpenOrCreate(string dataPath)
  {
    return new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .OpenOrCreate();
  }
}
