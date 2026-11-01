using ZoneTree.Transactional.Conflicts;

namespace ZoneTree.UnitTests;

public sealed class KeyListTests
{
  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  public void DistinctKeysStayCorrectAcrossCompactionsAndLaterAppends(int order)
  {
    var random = new Random(42);
    var input = Enumerable.Range(0, 4096).Select(i => i / 3).ToArray();
    if (order == 1)
      Array.Reverse(input);
    else if (order == 2)
      random.Shuffle(input);
    var list = new KeyList<int>(Comparer<int>.Default);
    var expected = new SortedSet<int>();
    foreach (var key in input)
    {
      list.Add(key);
      expected.Add(key);
      if (expected.Count % 61 == 0)
        Assert.That(list.ToDistinctArray(), Is.EqualTo(expected));
    }
    Assert.That(list.ToDistinctArray(), Is.EqualTo(expected));
  }

  [Test]
  public void OrderedDistinctKeysNeedOnlyLinearComparisons()
  {
    const int count = 4096;
    var comparisons = 0;
    var comparer = Comparer<int>.Create((x, y) =>
    {
      ++comparisons;
      return x.CompareTo(y);
    });
    var list = new KeyList<int>(comparer);
    for (var key = 0; key < count; ++key)
    {
      list.Add(key);
      if (key % 61 == 0)
        Assert.That(list.ToDistinctArray(), Is.EqualTo(Enumerable.Range(0, key + 1)));
    }
    Assert.That(list.ToDistinctArray(), Is.EqualTo(Enumerable.Range(0, count)));
    Assert.That(comparisons, Is.LessThan(2 * count));
  }

  [Test]
  public void AnOutOfOrderAppendAfterAnOrderedPrefixStillSortsAndDeduplicates()
  {
    var list = new KeyList<int>(Comparer<int>.Default);
    for (var key = 0; key < 1000; ++key)
      list.Add(key);
    Assert.That(list.ToDistinctArray(), Is.EqualTo(Enumerable.Range(0, 1000)));
    list.Add(-1);
    list.Add(500);
    list.Add(1000);
    Assert.That(list.ToDistinctArray(), Is.EqualTo(Enumerable.Range(-1, 1002)));
  }

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
