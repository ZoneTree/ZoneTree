using System.Reflection;
using ZoneTree.Options;
using ZoneTree.Segments;
using ZoneTree.Segments.DiskSegmentVariations;
using ZoneTree.Serializers;

namespace ZoneTree.UnitTests;

public sealed class DiskSegmentReadCountTests
{
  [Test]
  public void FixedSizeKeyAndValueCacheHitsKeepReadCountBalanced()
  {
    AssertCacheHitsKeepReadCountBalanced(
        "FixedSizeKeyAndValueCacheHitsKeepReadCountBalanced",
        1,
        2,
        typeof(FixedSizeKeyAndValueDiskSegment<int, int>),
        factory => factory.DisableDeletion());
  }

  [Test]
  public void FixedSizeKeyCacheHitsKeepReadCountBalanced()
  {
    AssertCacheHitsKeepReadCountBalanced(
        "FixedSizeKeyCacheHitsKeepReadCountBalanced",
        1,
        "value-1",
        typeof(FixedSizeKeyDiskSegment<int, string>),
        factory => factory
            .DisableDeletion()
            .SetValueSerializer(new UnicodeStringSerializer()));
  }

  [Test]
  public void FixedSizeValueCacheHitsKeepReadCountBalanced()
  {
    AssertCacheHitsKeepReadCountBalanced(
        "FixedSizeValueCacheHitsKeepReadCountBalanced",
        "key-1",
        2,
        typeof(FixedSizeValueDiskSegment<string, int>),
        factory => factory
            .DisableDeletion()
            .SetKeySerializer(new UnicodeStringSerializer()));
  }

  [Test]
  public void VariableSizeCacheHitsKeepReadCountBalanced()
  {
    AssertCacheHitsKeepReadCountBalanced(
        "VariableSizeCacheHitsKeepReadCountBalanced",
        "key-1",
        "value-1",
        typeof(VariableSizeDiskSegment<string, string>),
        factory => factory
            .DisableDeletion()
            .SetKeySerializer(new UnicodeStringSerializer())
            .SetValueSerializer(new UnicodeStringSerializer()));
  }

  static void AssertCacheHitsKeepReadCountBalanced<TKey, TValue>(
      string testName,
      TKey key,
      TValue value,
      Type expectedSegmentType,
      Func<ZoneTreeFactory<TKey, TValue>, ZoneTreeFactory<TKey, TValue>> configureFactory)
  {
    var dataPath = "data/" + testName;
    DeleteDirectory(dataPath);

    try
    {
      using var zoneTree = configureFactory(new ZoneTreeFactory<TKey, TValue>())
          .SetDataDirectory(dataPath)
          .SetWriteAheadLogDirectory(dataPath)
          .ConfigureWriteAheadLogOptions(options =>
              options.Mode = WriteAheadLogMode.None)
          .ConfigureDiskSegmentOptions(options =>
          {
            options.Mode = DiskSegmentMode.SingleDiskSegment;
            options.KeyCacheSize = 8;
            options.ValueCacheSize = 8;
          })
          .OpenOrCreate();

      zoneTree.Upsert(key, value);
      zoneTree.Maintenance.MoveMutableSegmentForward();
      zoneTree.Maintenance.StartMergeOperation().Join();

      var diskSegment = zoneTree.Maintenance.DiskSegment;
      Assert.That(diskSegment.GetType(), Is.EqualTo(expectedSegmentType));

      Assert.That(diskSegment.GetKey(0), Is.EqualTo(key));
      AssertReadCountIsZero(diskSegment, "key cache warm-up");
      Assert.That(diskSegment.GetKey(0), Is.EqualTo(key));
      AssertReadCountIsZero(diskSegment, "key cache hit");

      Assert.That(diskSegment.GetValue(0), Is.EqualTo(value));
      AssertReadCountIsZero(diskSegment, "value cache warm-up");
      Assert.That(diskSegment.GetValue(0), Is.EqualTo(value));
      AssertReadCountIsZero(diskSegment, "value cache hit");
    }
    finally
    {
      DeleteDirectory(dataPath);
    }
  }

  static void AssertReadCountIsZero<TKey, TValue>(
      IDiskSegment<TKey, TValue> diskSegment,
      string operation)
  {
    Assert.That(
        GetReadCount(diskSegment),
        Is.Zero,
        $"ReadCount must remain balanced after {operation}.");
  }

  static int GetReadCount<TKey, TValue>(IDiskSegment<TKey, TValue> diskSegment)
  {
    for (var type = diskSegment.GetType(); type != null; type = type.BaseType)
    {
      var property = type.GetProperty(
          "ActiveReadCount",
          BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
      if (property != null)
        return (int)property.GetValue(diskSegment);
    }

    throw new MissingMemberException(
        diskSegment.GetType().FullName,
        "ActiveReadCount");
  }

  static void DeleteDirectory(string path)
  {
    if (Directory.Exists(path))
      Directory.Delete(path, true);
  }
}
