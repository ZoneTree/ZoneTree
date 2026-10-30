using ZoneTree.Transactional.Conflicts;

namespace ZoneTree.UnitTests;

public sealed class KeyListTests
{
  /// <summary>
  /// Compares integers, and throws at a chosen comparison.
  /// </summary>
  sealed class FailingComparer(int failAt) : IComparer<int>
  {
    int Comparisons;

    public bool HasFailed { get; private set; }

    public int Compare(int x, int y)
    {
      if (++Comparisons == failAt)
      {
        HasFailed = true;
        throw new InvalidOperationException("The comparer failed.");
      }
      return x.CompareTo(y);
    }
  }

  [TestCase(5)]
  [TestCase(64)]
  public void AFailedSortKeepsEveryKey(int count)
  {
    // Rollback reads the list to find every written key. Descending keys make
    // the sort move elements; 64 keys also compact the list while adding.
    var keys = Enumerable.Range(1, count).Reverse().ToArray();
    for (var failAt = 1; ; ++failAt)
    {
      var comparer = new FailingComparer(failAt);
      var list = new KeyList<int>(comparer);
      try
      {
        foreach (var key in keys)
          list.Add(key);
        Assert.That(list.ToDistinctArray(), Is.EqualTo(keys.Reverse()));
      }
      catch (InvalidOperationException)
      {
      }
      Assert.That(list.Items, Is.EquivalentTo(keys), $"comparer failure at comparison {failAt}");
      if (!comparer.HasFailed)
        break;
    }
  }
}
