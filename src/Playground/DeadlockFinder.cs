using System.Diagnostics;
using ZoneTree.Collections;
using ZoneTree.Comparers;

namespace Playground;

public class DeadlockFinder
{
  BPlusTree<long, long> Tree;

  Stopwatch Stopwatch;

  public bool UpsertOnlyMode = true;

  void TreeLoop(int taskNo)
  {
    var tree = Tree;
    try
    {
      const int recCount = 100_000_000;
      const int upsertLogFrequency = 1_000_000;
      const int iterationLogFrequency = 10_000_000;
      const int iterationYieldFrequency = 1000;
      var rand = Random.Shared;

      void resetTree()
      {
        if (Stopwatch.Elapsed.TotalSeconds > 5 && tree.Length > recCount * 0.1)
        {
          {
            Console.WriteLine("Resetting tree." + tree.Length);
            Stopwatch.Restart();
            tree = new BPlusTree<long, long>(new Int64ComparerAscending(), BPlusTreeLockMode.NodeLevelMonitor);
          }
        }
      }
      if (UpsertOnlyMode || taskNo % 3 == 0)
      {
        uint i = 0;
        while (true)
        {
          if (taskNo == 0)
            resetTree();
          tree.Upsert(rand.Next() % recCount, 3, out _);
          ++i;
          if (i % upsertLogFrequency == 0)
          {
            Console.WriteLine($"Upsert():{i}      task :{taskNo}");
          }
        }
      }
      else if (taskNo % 3 == 1)
      {
        uint i = 0;
        while (true)
        {
          var iterator = tree.GetFirstIterator();
          while (iterator.HasNext())
          {
            iterator.Next();
            ++i;
            if (rand.Next() % iterationYieldFrequency == 1)
              Thread.Yield();
            if (i % iterationLogFrequency == 0)
            {
              Console.WriteLine($"Next():{i}      task :{taskNo}");
            }
          }
        }
      }
      else
      {
        uint i = 0;
        while (true)
        {
          var iterator = tree.GetLastIterator();
          while (iterator.HasPrevious())
          {
            iterator.Previous();
            ++i;
            if (rand.Next() % iterationYieldFrequency == 1)
              Thread.Yield();

            if (i % iterationLogFrequency == 0)
            {
              Console.WriteLine($"Previous():{i}      task :{taskNo}");
            }
          }
        }
      }
    }
    catch (Exception e)
    {
      Console.WriteLine(e.ToString());
    }
  }

  public void RunDeadLockFinder()
  {
    Tree = new BPlusTree<long, long>(new Int64ComparerAscending(), BPlusTreeLockMode.NodeLevelMonitor);
    Stopwatch = Stopwatch.StartNew();
    Thread t0 = null;
    for (var i = 0; i < 100; ++i)
    {
      var k = i;
      var t = new Thread(() => TreeLoop(k));
      t.Start();
      t0 = t;
    }
    t0.Join();
  }
}
