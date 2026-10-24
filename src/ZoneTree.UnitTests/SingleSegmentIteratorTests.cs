using ZoneTree.AbstractFileStream;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

public sealed class SingleSegmentIteratorTests
{
  [Test]
  public void RepeatedSeeksRefreshAndDeletionKeepIteratorPosition(
      [Values] bool reverse,
      [Values(0, 3)] int prefetchSize,
      [Values] bool merge)
  {
    using var tree = new ZoneTreeFactory<int, int>(new InMemoryFileStreamProvider())
        .SetDataDirectory("single-iterator-tests")
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.None)
        .OpenOrCreate();
    for (var i = 1; i <= 9; i++) tree.Upsert(i, i);
    tree.ForceDelete(4);
    if (merge)
    {
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Maintenance.StartMergeOperation().Join();
    }

    var options = new IteratorOptions
    {
      IteratorType = IteratorType.NoRefresh,
      DiskSegmentPrefetchSize = prefetchSize
    };
    using var iterator = reverse ? tree.CreateReverseIterator(options) : tree.CreateIterator(options);
    var expected = Enumerable.Range(1, 9).Where(i => i != 4).ToArray();
    if (reverse) Array.Reverse(expected);
    for (var repetition = 0; repetition < 2; repetition++)
    {
      iterator.SeekFirst();
      foreach (var key in expected)
      {
        Assert.That(iterator.Next(), Is.True);
        Assert.That(iterator.CurrentKey, Is.EqualTo(key));
      }
      Assert.That(iterator.Next(), Is.False);
      Assert.That(iterator.Next(), Is.False);
      Assert.That(iterator.HasCurrent, Is.False);
    }
    iterator.Seek(5);
    Assert.That(iterator.Next(), Is.True);
    Assert.That(iterator.CurrentKey, Is.EqualTo(5));
    iterator.Refresh();
    Assert.That(iterator.Next(), Is.True);
    Assert.That(iterator.CurrentKey, Is.EqualTo(reverse ? 3 : 6));

    // Refresh from a single segment to a merge of mutable and disk data.
    tree.Upsert(5, 50);
    tree.Upsert(10, 10);
    tree.ForceDelete(6);
    iterator.Refresh();
    iterator.Seek(5);
    Assert.That(iterator.Next(), Is.True);
    Assert.That(iterator.CurrentValue, Is.EqualTo(50));
    Assert.That(iterator.Next(), Is.True);
    Assert.That(iterator.CurrentKey, Is.EqualTo(reverse ? 3 : 7));
  }
}
