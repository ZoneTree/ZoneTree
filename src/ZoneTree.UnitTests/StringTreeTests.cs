using Newtonsoft.Json.Linq;
using ZoneTree.AbstractFileStream;
using ZoneTree.Comparers;
using ZoneTree.Exceptions;
using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class StringTreeTests
{
  [Test]
  public void NullStringKeyTest()
  {
    var dataPath = "data/NullStringKeyTest";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var zoneTree = new ZoneTreeFactory<string, string>()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    var keys = new[]
    {
            null, "", "Abc", "Cbc", "Dbc", "zWithNullValue"
        };
    var values = new[]
    {
            "Zbc", "DDD", "Abc", "Cbc", "Dbc", null
        };
    for (var i = 0; i < keys.Length; i++)
    {
      zoneTree.Upsert(keys[i], values[i]);
    }

    using var iterator = zoneTree.CreateIterator();
    var j = 0;
    while (iterator.Next())
    {
      Assert.That(iterator.CurrentKey, Is.EqualTo(keys[j]));
      Assert.That(iterator.CurrentValue, Is.EqualTo(values[j]));
      ++j;
    }
    var maintenance = zoneTree.Maintenance;
    maintenance.MoveMutableSegmentForward();
    maintenance.StartMergeOperation()?.Join();

    using var iterator2 = zoneTree.CreateIterator();
    j = 0;
    while (iterator2.Next())
    {
      Assert.That(iterator2.CurrentKey, Is.EqualTo(keys[j]));
      Assert.That(iterator2.CurrentValue, Is.EqualTo(values[j]));
      ++j;
    }
    zoneTree.Maintenance.Drop();
  }

  [Test]
  public void TestSingleCharacter()
  {
    var dataPath = "data/TestSingleCharacter";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    for (var i = 0; i < 2; ++i)
    {
      using var db = new ZoneTreeFactory<string, int>()
          .DisableDeletion()
          .SetDataDirectory(dataPath)
          .OpenOrCreate();
      db.Upsert("0", 123);
    }
  }

  [Test]
  public void HelloWorldTest()
  {
    var dataPath = "data/HelloWorldTest";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var zoneTree = new ZoneTreeFactory<int, string>()
        .SetDataDirectory(dataPath)
        .OpenOrCreate();
    zoneTree.Upsert(39, "Hello Zone Tree");
    zoneTree.TryGet(39, out var value);
    Assert.That(value, Is.EqualTo("Hello Zone Tree"));
  }

  [Test]
  public void HelloWorldTest2()
  {
    var dataPath = "data/HelloWorldTest2";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    using var zoneTree = new ZoneTreeFactory<int, string>()
      .SetComparer(new Int32ComparerAscending())
      .SetDataDirectory(dataPath)
      .SetKeySerializer(new Int32Serializer())
      .SetValueSerializer(new Utf8StringSerializer())
      .OpenOrCreate();

    zoneTree.Upsert(39, "Hello Zone Tree!");

    zoneTree.TryGet(39, out var value);
    Assert.That(value, Is.EqualTo("Hello Zone Tree!"));
    // The lock keeps other locked writers of the key out between the read and the write.
    using (zoneTree.Lock(39))
    {
      zoneTree.TryGet(39, out var current);
      zoneTree.Upsert(39, current + "b");
    }
    zoneTree.TryGet(39, out value);
    Assert.That(value, Is.EqualTo("Hello Zone Tree!b"));
  }

  [Test]
  public void HelloWorldTest3()
  {
    var dataPath = "data/HelloWorldTest3";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);

    var tree = new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .OpenOrCreate();
    tree.Upsert(1, 0);
    // The value 0 represents deleted record.
    Assert.That(tree.Count(), Is.EqualTo(0));
    tree.Upsert(1, 2);
    Assert.That(tree.Count(), Is.EqualTo(1));
  }
}
