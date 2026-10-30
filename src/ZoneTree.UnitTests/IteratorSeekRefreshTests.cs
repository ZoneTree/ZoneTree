using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;

namespace ZoneTree.UnitTests;

public sealed class IteratorSeekRefreshTests
{
  [TestCase(false)]
  [TestCase(true)]
  public void SeekSurvivesASegmentMoveBeforeTheNextRead(bool reverse)
  {
    using var tree = new ZoneTreeFactory<int, int>(new InMemoryFileStreamProvider())
        .SetDataDirectory("data/" + nameof(SeekSurvivesASegmentMoveBeforeTheNextRead) + reverse)
        .SetLogger(new ConsoleLogger(LogLevel.Error))
        .OpenOrCreate();
    for (var i = 0; i < 10; ++i)
      tree.Upsert(i, i);
    using var iterator = reverse ?
        tree.CreateReverseIterator(IteratorType.AutoRefresh) :
        tree.CreateIterator(IteratorType.AutoRefresh);
    iterator.Seek(5);
    // The move makes the iterator refresh its segments on the next read.
    tree.Maintenance.MoveMutableSegmentForward();
    Assert.That(iterator.Next(), Is.True);
    Assert.That(iterator.CurrentKey, Is.EqualTo(5));
    Assert.That(iterator.Next(), Is.True);
    Assert.That(iterator.CurrentKey, Is.EqualTo(reverse ? 4 : 6));
    tree.Maintenance.Drop();
  }
}
