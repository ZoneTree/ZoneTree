using ZoneTree.Collections.BTree;
using ZoneTree.Collections.BTree.Lock;
using ZoneTree.Comparers;

namespace ZoneTree.Transactional.Conflicts;

/// <summary>
/// Maps each key written by a recent commit to the timestamp of its latest commit.
/// A transaction conflicts with the keys committed after its snapshot timestamp.
/// </summary>
/// <remarks>
/// <para>
/// Commits add keys one at a time under the commit lock of the tree, and any thread
/// can look up a key without a lock. A lookup that races with a commit may miss its
/// keys, so a commit validates again under the commit lock.
/// </para>
/// <para>
/// The keys live in generations, oldest first, and a key in a newer generation has a
/// newer commit. A generation is dropped as a whole once every active writer's snapshot
/// includes its newest commit, so forgetting keys costs nothing per key. The last
/// generation stays until it is full, because otherwise a single writer would build
/// a new one for every commit. Its keys cannot conflict once the snapshots include them.
/// </para>
/// <para>
/// A long-running writer keeps every newer generation alive, and a lookup can visit all
/// of them. So a background thread merges generations once there are too many. It only
/// reads generations that have a newer one, because commits add keys to the last one only.
/// </para>
/// </remarks>
internal sealed class ConflictIndex<TKey>
{
  /// <summary>
  /// The number of keys that fills a generation. Small enough that a generation
  /// usually dies young under load, and that a lookup stays cheap.
  /// </summary>
  const int GenerationSize = 4096;

  /// <summary>
  /// The number of generations that starts a merge. Short transactions never reach it.
  /// </summary>
  const int MaximumGenerationCount = 16;

  sealed class Generation(IRefComparer<TKey> comparer, long newestCommit)
  {
    public readonly BTree<TKey, long> Keys = new(comparer, BTreeLockMode.NodeLevelMonitor);

    public long NewestCommit = newestCommit;
  }

  readonly Lock SyncRoot = new();

  readonly IRefComparer<TKey> Comparer;

  volatile Generation[] Generations = [];

  long prunedHorizon;

  // Guarded by SyncRoot.
  bool isMerging;

  public ConflictIndex(IRefComparer<TKey> comparer)
  {
    Comparer = comparer;
  }

  /// <summary>
  /// The number of keys that can still conflict. It counts a key once per generation.
  /// </summary>
  public int Count
  {
    get
    {
      var horizon = Volatile.Read(ref prunedHorizon);
      var count = 0;
      foreach (var generation in Generations)
      {
        if (Volatile.Read(ref generation.NewestCommit) <= horizon)
          continue;
        var iterator = new BTreeSeekableIterator<TKey, long>(generation.Keys);
        for (var found = iterator.SeekBegin(); found; found = iterator.Next())
        {
          if (iterator.CurrentValue > horizon)
            ++count;
        }
      }
      return count;
    }
  }

  public bool HasCommitAfter(in TKey key, long timestamp)
  {
    var generations = Generations;
    for (var i = generations.Length - 1; i >= 0; --i)
    {
      var generation = generations[i];
      if (Volatile.Read(ref generation.NewestCommit) <= timestamp)
        return false;
      if (generation.Keys.TryGetValue(in key, out var commitTimestamp))
        return commitTimestamp > timestamp;
    }
    return false;
  }

  public bool HasCommitAfter(List<TKey> keys, long timestamp)
  {
    foreach (var key in keys)
    {
      if (HasCommitAfter(in key, timestamp))
        return true;
    }
    return false;
  }

  public bool HasCommitAfter(IReadOnlyList<KeyRange<TKey>> ranges, long timestamp)
  {
    var generations = Generations;
    for (var i = generations.Length - 1; i >= 0; --i)
    {
      var generation = generations[i];
      if (Volatile.Read(ref generation.NewestCommit) <= timestamp)
        return false;
      var count = ranges.Count;
      for (var j = 0; j < count; ++j)
      {
        if (HasCommitAfter(generation, ranges[j], timestamp))
          return true;
      }
    }
    return false;
  }

  bool HasCommitAfter(Generation generation, in KeyRange<TKey> range, long timestamp)
  {
    var iterator = new BTreeSeekableIterator<TKey, long>(generation.Keys);
    var found = range.HasLow ?
        iterator.SeekToFirstGreaterOrEqualElement(in range.Low) :
        iterator.SeekBegin();
    for (; found; found = iterator.Next())
    {
      var key = iterator.CurrentKey;
      if (range.HasHigh && Comparer.Compare(in key, in range.High) > 0)
        return false;
      if (iterator.CurrentValue > timestamp)
        return true;
    }
    return false;
  }

  /// <summary>
  /// Adds the keys of a commit. Called under the commit lock of the tree,
  /// with increasing timestamps.
  /// </summary>
  public void Add(TKey[] keys, long commitTimestamp)
  {
    var generation = GetGenerationToAdd(commitTimestamp);
    foreach (var key in keys)
      generation.Keys.Upsert(in key, in commitTimestamp, out _);
  }

  /// <summary>
  /// Claims a generation for the commit before adding any key, so that
  /// pruning cannot drop the generation while the keys are going in.
  /// </summary>
  Generation GetGenerationToAdd(long commitTimestamp)
  {
    lock (SyncRoot)
    {
      var generations = Generations;
      if (generations.Length > 0 && generations[^1].Keys.Length < GenerationSize)
      {
        var last = generations[^1];
        Volatile.Write(ref last.NewestCommit, commitTimestamp);
        return last;
      }
      var generation = new Generation(Comparer, commitTimestamp);
      Generations = [.. generations, generation];
      if (Generations.Length > MaximumGenerationCount && !isMerging)
      {
        isMerging = true;
        new Thread(MergeGenerations) { IsBackground = true, Name = MergeThreadName }.Start();
      }
      return generation;
    }
  }

  internal const string MergeThreadName = "ZoneTree conflict index merge";

  void MergeGenerations()
  {
    try
    {
      while (true)
      {
        Generation[] run;
        lock (SyncRoot)
        {
          run = SelectGenerationsToMerge(Generations);
          if (run == null)
          {
            isMerging = false;
            return;
          }
        }
        ReplaceGenerations(run, Merge(run));
      }
    }
    catch (Exception)
    {
      // The comparer failed. The generations are as they were, so lookups stay
      // correct and only slower. The next generation starts another merge.
      lock (SyncRoot)
        isMerging = false;
    }
  }

  /// <summary>
  /// Selects the newest generations with a similar total size, so that a key takes
  /// part in few merges. The sizes end up growing with age, which keeps a tree of any
  /// size within a few generations.
  /// </summary>
  /// <returns>Adjacent generations, oldest first, or null if no merge is needed.</returns>
  static Generation[] SelectGenerationsToMerge(Generation[] generations)
  {
    if (generations.Length <= MaximumGenerationCount)
      return null;
    // Commits are still adding keys to the last generation.
    var end = generations.Length - 1;
    var start = end - 1;
    long size = generations[start].Keys.Length;
    while (start > 0 && generations[start - 1].Keys.Length <= size * 2)
    {
      --start;
      size += generations[start].Keys.Length;
    }
    if (end - start < 2)
      --start;
    return generations[start..end];
  }

  /// <summary>
  /// Builds a generation from the given ones. A newer commit of a key replaces an
  /// older one, and a key that no writer can conflict with is left out.
  /// </summary>
  Generation Merge(Generation[] run)
  {
    var horizon = Volatile.Read(ref prunedHorizon);
    var merged = new Generation(Comparer, Volatile.Read(ref run[^1].NewestCommit));
    foreach (var generation in run)
    {
      var iterator = new BTreeSeekableIterator<TKey, long>(generation.Keys);
      for (var found = iterator.SeekBegin(); found; found = iterator.Next())
      {
        var commitTimestamp = iterator.CurrentValue;
        if (commitTimestamp <= horizon)
          continue;
        var key = iterator.CurrentKey;
        merged.Keys.Upsert(in key, in commitTimestamp, out _);
      }
    }
    return merged;
  }

  void ReplaceGenerations(Generation[] run, Generation merged)
  {
    lock (SyncRoot)
    {
      var generations = Generations;
      // Pruning drops the oldest generations first, and only this
      // thread merges, so what is left of the run is still adjacent.
      var end = Array.IndexOf(generations, run[^1]);
      if (end < 0)
        return;
      var start = Math.Max(0, end - run.Length + 1);
      Generations = [.. generations[..start], merged, .. generations[(end + 1)..]];
    }
  }

  /// <summary>
  /// Forgets the keys that no writer can conflict with.
  /// </summary>
  /// <param name="horizon">The oldest snapshot timestamp of the active writers,
  /// or the latest commit timestamp without one. A commit that has not finished
  /// is newer than this, so its generation stays.</param>
  public void Prune(long horizon)
  {
    Volatile.Write(ref prunedHorizon, horizon);
    var generations = Generations;
    if (generations.Length == 0 || !CanDrop(generations, 0, horizon))
      return;
    lock (SyncRoot)
    {
      generations = Generations;
      var dropCount = 0;
      while (dropCount < generations.Length && CanDrop(generations, dropCount, horizon))
        ++dropCount;
      if (dropCount > 0)
        Generations = generations[dropCount..];
    }
  }

  static bool CanDrop(Generation[] generations, int index, long horizon)
  {
    var generation = generations[index];
    return Volatile.Read(ref generation.NewestCommit) <= horizon &&
        (index < generations.Length - 1 || generation.Keys.Length >= GenerationSize);
  }
}
