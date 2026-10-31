using System.Reflection;
using ZoneTree.Comparers;
using ZoneTree.Transactional.Conflicts;

namespace ZoneTree.UnitTests;

public sealed class ConflictIndexTests
{
  /// <summary>
  /// More keys than a generation holds, so the next commit starts a new generation.
  /// </summary>
  const int FullGeneration = 5000;

  static ConflictIndex<int> CreateIndex() => new(new Int32ComparerAscending());

  static int[] Keys(int first, int count = FullGeneration)
      => Enumerable.Range(first, count).ToArray();

  static int GenerationCount(ConflictIndex<int> index)
  {
    var field = typeof(ConflictIndex<int>)
        .GetField("Generations", BindingFlags.Instance | BindingFlags.NonPublic);
    return ((Array)field.GetValue(index)).Length;
  }

  static KeyRange<int>[] Range(int? low, int? high) =>
  [
    new KeyRange<int>
    {
      Low = low ?? 0,
      HasLow = low.HasValue,
      High = high ?? 0,
      HasHigh = high.HasValue
    }
  ];

  [Test]
  public void AKeyInAnOlderGenerationConflicts()
  {
    var index = CreateIndex();
    index.Add([1], 10);
    index.Add(Keys(100), 20);
    index.Add(Keys(10_000), 30);
    index.Add([20_000], 40);
    Assert.That(GenerationCount(index), Is.EqualTo(3));

    Assert.That(index.HasCommitAfter(1, 9), Is.True);
    Assert.That(index.HasCommitAfter(1, 10), Is.False);
    Assert.That(index.HasCommitAfter(100, 19), Is.True);
    Assert.That(index.HasCommitAfter(10_000, 29), Is.True);
    Assert.That(index.HasCommitAfter(20_000, 39), Is.True);
    Assert.That(index.HasCommitAfter(20_001, 0), Is.False);
    Assert.That(index.HasCommitAfter(new List<int> { 20_001, 1 }, 9), Is.True);
    Assert.That(index.HasCommitAfter(new List<int> { 20_001, 1 }, 10), Is.False);
  }

  [Test]
  public void TheNewestCommitOfAKeyDecides()
  {
    var index = CreateIndex();
    index.Add([1], 10);
    index.Add(Keys(100), 20);
    index.Add([1], 30);
    index.Add([1], 40);
    Assert.That(GenerationCount(index), Is.EqualTo(2));

    Assert.That(index.HasCommitAfter(1, 5), Is.True);
    Assert.That(index.HasCommitAfter(1, 39), Is.True);
    Assert.That(index.HasCommitAfter(1, 40), Is.False);
  }

  [Test]
  public void RangesFindKeysInEveryGeneration()
  {
    var index = CreateIndex();
    index.Add([5], 10);
    index.Add(Keys(1000), 20);
    index.Add(Keys(10_000), 30);
    Assert.That(GenerationCount(index), Is.EqualTo(2));

    // A key in the older generation.
    Assert.That(index.HasCommitAfter(Range(1, 10), 9), Is.True);
    Assert.That(index.HasCommitAfter(Range(1, 10), 10), Is.False);
    // The bounds are inclusive.
    Assert.That(index.HasCommitAfter(Range(5, 5), 9), Is.True);
    Assert.That(index.HasCommitAfter(Range(1, 4), 9), Is.False);
    Assert.That(index.HasCommitAfter(Range(6, 10), 9), Is.False);
    // A missing bound is unbounded.
    Assert.That(index.HasCommitAfter(Range(null, 10), 9), Is.True);
    Assert.That(index.HasCommitAfter(Range(10_000 + FullGeneration - 1, null), 29), Is.True);
    Assert.That(index.HasCommitAfter(Range(10_000 + FullGeneration, null), 0), Is.False);
    Assert.That(index.HasCommitAfter(Range(null, null), 29), Is.True);
    Assert.That(index.HasCommitAfter(Range(null, null), 30), Is.False);
    // A range that holds no keys, one past every leaf of the older generation.
    Assert.That(index.HasCommitAfter(Range(7000, 9000), 0), Is.False);
    Assert.That(index.HasCommitAfter(Range(10, 1), 0), Is.False);
  }

  [Test]
  public void ARangeReadsPastOlderCommitsToTheNewerOne()
  {
    var index = CreateIndex();
    index.Add(Keys(0, 1000), 10);
    index.Add([1000], 20);

    Assert.That(index.HasCommitAfter(Range(0, 2000), 15), Is.True);
    Assert.That(index.HasCommitAfter(Range(0, 999), 15), Is.False);
  }

  [Test]
  public void PruningDropsTheGenerationsNoWriterNeeds()
  {
    var index = CreateIndex();
    index.Add(Keys(0), 10);
    index.Add(Keys(10_000), 20);
    index.Add(Keys(20_000), 30);
    Assert.That(GenerationCount(index), Is.EqualTo(3));

    index.Prune(9);
    Assert.That(GenerationCount(index), Is.EqualTo(3));

    index.Prune(20);
    Assert.That(GenerationCount(index), Is.EqualTo(1));
    Assert.That(index.Count, Is.EqualTo(FullGeneration));
    Assert.That(index.HasCommitAfter(20_000, 20), Is.True);

    // A full last generation goes too, and the next commit starts another.
    index.Prune(30);
    Assert.That(GenerationCount(index), Is.Zero);
    Assert.That(index.Count, Is.Zero);
    Assert.That(index.HasCommitAfter(20_000, 0), Is.False);
    index.Add([1], 40);
    Assert.That(GenerationCount(index), Is.EqualTo(1));
    Assert.That(index.HasCommitAfter(1, 39), Is.True);
  }

  [Test]
  public void ASingleWriterReusesTheLastGeneration()
  {
    var index = CreateIndex();
    for (var i = 1; i <= 100; ++i)
    {
      index.Add([i], i);
      index.Prune(i);
      Assert.That(index.Count, Is.Zero);
    }
    Assert.That(GenerationCount(index), Is.EqualTo(1));

    index.Add([1], 101);
    Assert.That(index.Count, Is.EqualTo(1));
    Assert.That(index.HasCommitAfter(1, 100), Is.True);
    Assert.That(index.HasCommitAfter(2, 100), Is.False);
  }

  [Test]
  public void CountIgnoresTheKeysOfOlderCommits()
  {
    var index = CreateIndex();
    index.Add([1, 2, 3], 10);
    index.Prune(10);
    index.Add([4], 20);
    Assert.That(index.Count, Is.EqualTo(1));
    index.Add([1], 30);
    Assert.That(index.Count, Is.EqualTo(2));
  }

  [Test]
  public void AFailedAddLeavesItsKeysUntilTheyArePruned()
  {
    var comparer = new FailingAfterComparer();
    var index = new ConflictIndex<int>(comparer);
    index.Add([1, 2, 3], 10);

    comparer.Failures = 1;
    Assert.Throws<InvalidOperationException>(() => index.Add([4, 5, 6], 20));
    Assert.That(comparer.Failures, Is.Zero);

    Assert.That(index.HasCommitAfter(1, 9), Is.True);
    index.Prune(20);
    Assert.That(index.Count, Is.Zero);
    Assert.That(index.HasCommitAfter(1, 9), Is.True, "the last generation stays");
    Assert.That(index.HasCommitAfter(1, 10), Is.False);
  }

  /// <summary>
  /// A long-running writer keeps every generation alive, as it holds the horizon.
  /// </summary>
  static void AddFullGenerations(ConflictIndex<int> index, int count)
  {
    for (var i = 0; i < count; ++i)
      index.Add(Keys(i * FullGeneration), i + 1);
  }

  static void AssertEveryKeyIsFound(ConflictIndex<int> index, int generationCount)
  {
    for (var i = 0; i < generationCount; ++i)
    {
      var key = i * FullGeneration + i;
      Assert.That(index.HasCommitAfter(key, i), Is.True, $"key {key}");
      Assert.That(index.HasCommitAfter(key, i + 1), Is.False, $"key {key}");
    }
  }

  static bool IsMerging(ConflictIndex<int> index) =>
      (bool)typeof(ConflictIndex<int>)
          .GetField("isMerging", BindingFlags.Instance | BindingFlags.NonPublic)
          .GetValue(index);

  [Test]
  public void GenerationsMergeWhileAWriterHoldsTheHorizon()
  {
    const int count = 128;
    var index = CreateIndex();
    AddFullGenerations(index, count);

    Assert.That(SpinWait.SpinUntil(() => !IsMerging(index), TimeSpan.FromSeconds(60)), Is.True);
    Assert.That(GenerationCount(index), Is.LessThanOrEqualTo(16));
    AssertEveryKeyIsFound(index, count);
    Assert.That(index.Count, Is.EqualTo(count * FullGeneration));

    index.Prune(count);
    Assert.That(index.Count, Is.Zero);
    Assert.That(GenerationCount(index), Is.Zero);
  }

  [Test]
  public void MergingKeepsTheNewestCommitOfAKey()
  {
    const int count = 64;
    var index = CreateIndex();
    for (var i = 0; i < count; ++i)
      index.Add([1, .. Keys((i + 1) * FullGeneration)], i + 1);

    Assert.That(SpinWait.SpinUntil(() => !IsMerging(index), TimeSpan.FromSeconds(60)), Is.True);
    Assert.That(index.HasCommitAfter(1, count - 1), Is.True);
    Assert.That(index.HasCommitAfter(1, count), Is.False);
  }

  [Test]
  public void MergingLeavesOutKeysNoWriterCanConflictWith()
  {
    const int count = 64;
    const int horizon = 32;
    var index = CreateIndex();
    AddFullGenerations(index, count);
    index.Prune(horizon);

    Assert.That(SpinWait.SpinUntil(() => !IsMerging(index), TimeSpan.FromSeconds(60)), Is.True);
    for (var i = 0; i < count; ++i)
    {
      var key = i * FullGeneration;
      var commit = i + 1;
      // A writer's snapshot is at or after the horizon.
      var snapshot = Math.Max(horizon, commit - 1);
      Assert.That(index.HasCommitAfter(key, snapshot), Is.EqualTo(commit > horizon), $"key {key}");
    }
  }

  [Test]
  public void LookupsFindEveryFinishedAddWhileGenerationsMerge()
  {
    const int count = 400_000;
    var index = CreateIndex();
    var published = 0;
    using var start = new Barrier(5);
    var writer = Task.Factory.StartNew(() =>
    {
      start.SignalAndWait();
      for (var key = 1; key <= count; ++key)
      {
        index.Add([key], key);
        Volatile.Write(ref published, key);
      }
    }, TaskCreationOptions.LongRunning);
    var readers = Enumerable.Range(0, 4).Select(seed => Task.Factory.StartNew(() =>
    {
      start.SignalAndWait();
      var random = new Random(seed);
      while (Volatile.Read(ref published) < count)
      {
        var last = Volatile.Read(ref published);
        if (last == 0)
          continue;
        // Nothing is pruned, so every finished add stays.
        var key = 1 + random.Next(last);
        Assert.That(index.HasCommitAfter(in key, key - 1), Is.True, $"key {key} of {last}");
        Assert.That(index.HasCommitAfter(in key, key), Is.False, $"key {key} of {last}");
      }
    }, TaskCreationOptions.LongRunning)).ToArray();

    Assert.That(Task.WaitAll([writer, .. readers], TimeSpan.FromSeconds(120)), Is.True);
    Assert.That(SpinWait.SpinUntil(() => !IsMerging(index), TimeSpan.FromSeconds(60)), Is.True);
    Assert.That(GenerationCount(index), Is.LessThanOrEqualTo(16));
  }

  [Test]
  public void AFailedMergeLeavesTheGenerationsCorrect()
  {
    const int count = 32;
    var index = new ConflictIndex<int>(new FailingOnMergeThreadComparer());
    AddFullGenerations(index, count);

    Assert.That(SpinWait.SpinUntil(() => !IsMerging(index), TimeSpan.FromSeconds(60)), Is.True);
    Assert.That(GenerationCount(index), Is.EqualTo(count));
    AssertEveryKeyIsFound(index, count);
  }

  sealed class FailingOnMergeThreadComparer : IRefComparer<int>
  {
    public int Compare(in int x, in int y)
    {
      if (Thread.CurrentThread.Name == ConflictIndex<int>.MergeThreadName)
        throw new InvalidOperationException("The comparer failed.");
      return x.CompareTo(y);
    }
  }

  sealed class FailingAfterComparer : IRefComparer<int>
  {
    public int Failures;

    public int Compare(in int x, in int y)
    {
      if (Failures > 0)
      {
        --Failures;
        throw new InvalidOperationException("The comparer failed.");
      }
      return x.CompareTo(y);
    }
  }

  [Test]
  public void LookupsFindEveryFinishedAddWhileGenerationsTurnOver()
  {
    const int count = 200_000;
    const int kept = 5000;
    var index = CreateIndex();
    var published = 0;
    var horizon = 0;
    using var start = new Barrier(5);
    var writer = Task.Factory.StartNew(() =>
    {
      start.SignalAndWait();
      for (var key = 1; key <= count; ++key)
      {
        index.Add([key], key);
        Volatile.Write(ref published, key);
        if (key % 64 == 0)
        {
          // Published first, so a reader that finds a key pruned also sees why.
          Volatile.Write(ref horizon, Math.Max(0, key - kept));
          index.Prune(Volatile.Read(ref horizon));
        }
      }
    }, TaskCreationOptions.LongRunning);
    var readers = Enumerable.Range(0, 4).Select(seed => Task.Factory.StartNew(() =>
    {
      start.SignalAndWait();
      var random = new Random(seed);
      while (Volatile.Read(ref published) < count)
      {
        var last = Volatile.Read(ref published);
        if (last == 0)
          continue;
        var key = Math.Max(1, last - random.Next(kept / 2));
        // The writer can move past this key after it was picked. Only
        // a key at or below the horizon can be gone, as no writer needs it.
        if (!index.HasCommitAfter(in key, key - 1))
          Assert.That(Volatile.Read(ref horizon), Is.GreaterThanOrEqualTo(key), $"key {key} of {last}");
        Assert.That(index.HasCommitAfter(in key, key), Is.False, $"key {key} of {last}");
      }
    }, TaskCreationOptions.LongRunning)).ToArray();

    Assert.That(Task.WaitAll([writer, .. readers], TimeSpan.FromSeconds(120)), Is.True);
    Assert.That(GenerationCount(index), Is.LessThan(5), "the generations are dropped");
  }
}
