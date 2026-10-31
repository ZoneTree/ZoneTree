using ZoneTree.AbstractFileStream;
using ZoneTree.Options;

namespace ZoneTree.UnitTests;

/// <summary>
/// A point read must not copy the leaf of the mutable segment.
/// </summary>
public sealed class PointReadAllocationTests
{
  [Test]
  public void TransactionalPointReadDoesNotCopyTheLeaf()
  {
    const int keyCount = 20_000;
    const int readCount = 20_000;
    using var tree = new ZoneTreeFactory<int, int>(new InMemoryFileStreamProvider())
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.None)
        .CreateTransactional();
    using (var writer = tree.BeginTransaction())
    {
      for (var key = 0; key < keyCount; ++key)
        writer.Upsert(key, key + 1);
      writer.Commit();
    }

    using var reader = tree.BeginReadOnlyTransaction();
    // Compile the read path before measuring it.
    for (var key = 0; key < readCount; ++key)
      Assert.That(reader.TryGet(key % keyCount, out _), Is.True);

    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    for (var key = 0; key < readCount; ++key)
      reader.TryGet(key * 7 % keyCount, out _);
    var allocatedPerRead = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / readCount;

    TestContext.Out.WriteLine($"{allocatedPerRead} bytes per read.");
    Assert.That(allocatedPerRead, Is.LessThan(64));
  }
}
