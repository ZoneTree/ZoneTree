#undef USE_NODE_IDS

using ZoneTree.Collections.BTree.Lock;
using ZoneTree.Comparers;

namespace ZoneTree.Collections.BTree;

public sealed partial class BTree<TKey, TValue>
{
  public class Node
  {
    readonly ILocker Locker;

    public TKey[] Keys;

    public Node[] Children;

    public int Length;

    public bool IsFull => Keys.Length == Length;

#if USE_NODE_IDS
        static int IncrementalId = 0;

        public int Id;
#endif

    public Node(ILocker locker)
    {
      Locker = locker;
#if USE_NODE_IDS
            Id = Interlocked.Increment(ref IncrementalId);
#endif
    }

    public Node(ILocker locker, int nodeSize)
    {
      Locker = locker;
#if USE_NODE_IDS
            Id = Interlocked.Increment(ref IncrementalId);
#endif
      Keys = new TKey[nodeSize];
      Children = new Node[nodeSize + 1];
    }

    public Node(ILocker locker, int length, TKey[] keys, Node[] children)
    {
      Locker = locker;
      Length = length;
      Keys = keys;
      Children = children;
    }

    public bool TryGetPosition(
        IRefComparer<TKey> comparer,
        in TKey key,
        out int position)
    {
      var list = Keys;
      int l = 0, r = Length - 1;
      while (l <= r)
      {
        int m = (l + r) / 2;
        var rec = list[m];
        var res = comparer.Compare(in rec, in key);
        if (res == 0)
        {
          position = m;
          return true;
        }
        if (res < 0)
          l = m + 1;
        else
          r = m - 1;
      }
      position = r + 1;
      return false;
    }

    public void InsertKeyAndChild(int position, in TKey key, Node left, Node right)
    {
      // The increments are full fences: a reader sees the version
      // change before any of the changes, and the changes before it ends.
      Interlocked.Increment(ref version);
      var len = Length - position;
      if (len > 0)
      {
        Array.Copy(Keys, position, Keys, position + 1, len);
        Array.Copy(Children, position + 1, Children, position + 2, len);
      }
      Keys[position] = key;
      Children[position] = left;
      Children[position + 1] = right;
      ++Length;
      Interlocked.Increment(ref version);
    }

    int version;

    /// <summary>
    /// The version of the keys and children of an inner node, odd while they change.
    /// A reader that sees the same even version before and after reading
    /// the node has read a consistent state without locking it.
    /// </summary>
    public int Version => Volatile.Read(ref version);

    /// <summary>
    /// Tells whether the node is unchanged since a reader saw the version.
    /// The fence keeps the reads of the node before this check.
    /// </summary>
    public bool IsUnchangedSince(int seenVersion)
    {
      Interlocked.MemoryBarrier();
      return Volatile.Read(ref version) == seenVersion;
    }

    public void ReadLock()
    {
      Locker.ReadLock();
    }

    public void ReadUnlock()
    {
      Locker.ReadUnlock();
    }

    public void WriteLock()
    {
      Locker.WriteLock();
    }

    public bool TryEnterWriteLock(int millisecondsTimeout)
    {
      return Locker.TryEnterWriteLock(millisecondsTimeout);
    }

    public void WriteUnlock()
    {
      Locker.WriteUnlock();
    }

    public (Node left, Node right) Split(
        int middle, int nodeSize, ILocker locker1, ILocker locker2)
    {
      var left = new Node(locker1, nodeSize);
      var right = new Node(locker2, nodeSize);
      var len1 = middle;
      var len2 = Length - middle - 1;
      left.Length = len1;
      right.Length = len2;
      Array.Copy(Keys, 0, left.Keys, 0, len1);
      Array.Copy(Children, 0, left.Children, 0, len1 + 1);
      Array.Copy(Keys, middle + 1, right.Keys, 0, len2);
      Array.Copy(Children, middle + 1, right.Children, 0, len2 + 1);
      return (left, right);
    }

    public void Validate(IRefComparer<TKey> comparer)
    {
      if (Children == null)
        return;
      for (var i = 0; i < Length; ++i)
      {
        var key = Keys[i];
        Children[i].EnsureKeysAreSmallerThan(comparer, key);
        Children[i + 1].EnsureKeysAreGreaterOrEqualThan(comparer, key);
      }
      for (var i = 0; i <= Length; ++i)
      {
        Children[i].Validate(comparer);
      }
    }

    void EnsureKeysAreSmallerThan(IRefComparer<TKey> comparer, TKey parentKey)
    {
      for (var i = 0; i < Length; ++i)
      {
        var key = Keys[i];
        if (comparer.Compare(key, parentKey) >= 0)
          throw new Exception($"{key} >= {parentKey}");
      }
    }

    void EnsureKeysAreGreaterOrEqualThan(IRefComparer<TKey> comparer, TKey parentKey)
    {
      for (var i = 0; i < Length; ++i)
      {
        var key = Keys[i];
        if (comparer.Compare(key, parentKey) < 0)
          throw new Exception($"{key} < {parentKey}");
      }
    }

    public virtual Node CloneWithNoLock()
    {
      var len = Length;
      var node = new Node(NoLock.Instance, len, Keys, new Node[len + 1]);
      for (var i = 0; i <= len; ++i)
      {
        node.Children[i] = Children[i].CloneWithNoLock();
      }
      return node;
    }
  }
}
