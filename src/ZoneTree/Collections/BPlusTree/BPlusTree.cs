using ZoneTree.Collections.BPlusTreeLocks;
using ZoneTree.Comparers;
using ZoneTree.Core;

namespace ZoneTree.Collections;

/// <summary>
/// In memory B+Tree.
/// This class is thread-safe.
/// </summary>
/// <typeparam name="TKey">Key Type</typeparam>
/// <typeparam name="TValue">Value Type</typeparam>
public sealed partial class BPlusTree<TKey, TValue>
{
  ILocker TopLevelLocker;

  readonly int NodeSize = 128;

  readonly int LeafSize = 128;

  volatile Node Root;

  volatile LeafNode FirstLeafNode;

  volatile LeafNode LastLeafNode;

  // The last leaf once its parent links it. A write to a leaf that the search cannot
  // reach yet would be invisible after it completes, so writes use this field.
  volatile LeafNode LastLinkedLeafNode;

  public readonly IRefComparer<TKey> Comparer;

  volatile int _length;

  public int Length => _length;

  public readonly BPlusTreeLockMode LockMode;

  readonly bool HasNodeLocks;

  public IIncrementalIdProvider OpIndexProvider { get; }

  volatile bool _isReadOnly;

  public bool IsReadOnly { get => _isReadOnly; set => _isReadOnly = value; }

  public BPlusTree(
      IRefComparer<TKey> comparer,
      BPlusTreeLockMode lockMode,
      IIncrementalIdProvider indexOpProvider = null,
      int nodeSize = 128,
      int leafSize = 128)
  {
    // A split takes its pivot after the middle key of a full node,
    // so a node needs at least 2 keys.
    if (nodeSize < 2)
      throw new ArgumentOutOfRangeException(nameof(nodeSize), nodeSize, "A node holds at least 2 keys.");
    if (leafSize < 2)
      throw new ArgumentOutOfRangeException(nameof(leafSize), leafSize, "A leaf holds at least 2 keys.");
    NodeSize = nodeSize;
    LeafSize = leafSize;
    Comparer = comparer;
    OpIndexProvider = indexOpProvider ?? new IncrementalIdProvider();
    TopLevelLocker = lockMode switch
    {
      BPlusTreeLockMode.TopLevelMonitor => new MonitorLock(),
      BPlusTreeLockMode.TopLevelReaderWriter => new ReadWriteLock(),
      BPlusTreeLockMode.NoLock or BPlusTreeLockMode.NodeLevelMonitor or BPlusTreeLockMode.NodeLevelReaderWriter => NoLock.Instance,
      _ => throw new NotSupportedException(),
    };
    LockMode = lockMode;
    HasNodeLocks = lockMode is BPlusTreeLockMode.NodeLevelMonitor or BPlusTreeLockMode.NodeLevelReaderWriter;
    Root = new LeafNode(GetNodeLocker(), leafSize);
    FirstLeafNode = Root as LeafNode;
    LastLeafNode = FirstLeafNode;
    LastLinkedLeafNode = FirstLeafNode;
  }

  ILocker GetNodeLocker() => LockMode switch
  {
    BPlusTreeLockMode.TopLevelMonitor or
    BPlusTreeLockMode.TopLevelReaderWriter or
    BPlusTreeLockMode.NoLock => NoLock.Instance,

    BPlusTreeLockMode.NodeLevelMonitor => new MonitorLock(),
    BPlusTreeLockMode.NodeLevelReaderWriter => new ReadWriteLock(),
    _ => throw new NotSupportedException(),
  };

  public void WriteLock()
  {
    TopLevelLocker.WriteLock();
  }

  public void WriteUnlock()
  {
    TopLevelLocker.WriteUnlock();
  }

  public NodeIterator GetIteratorWithLastKeySmallerOrEqual(in TKey key)
  {
    var topLevelLocker = TopLevelLocker;
    try
    {
      topLevelLocker.ReadLock();
      var iterator = GetLeafNode(key).GetIterator(this);
      return iterator.SeekLastKeySmallerOrEqual(Comparer, in key);
    }
    finally
    {
      topLevelLocker.ReadUnlock();
    }
  }

  public NodeIterator GetIteratorWithFirstKeyGreaterOrEqual(in TKey key)
  {
    var topLevelLocker = TopLevelLocker;
    try
    {
      topLevelLocker.ReadLock();
      var iterator = GetLeafNode(key).GetIterator(this);
      return iterator.SeekFirstKeyGreaterOrEqual(Comparer, in key);
    }
    finally
    {
      topLevelLocker.ReadUnlock();
    }
  }

  public FrozenNodeIterator GetFrozenIteratorWithLastKeySmallerOrEqual(in TKey key)
  {
    var iterator = GetFrozenLeafNode(key).CreateFrozenIterator();
    return iterator.SeekLastKeySmallerOrEqual(Comparer, in key);
  }

  public FrozenNodeIterator GetFrozenIteratorWithFirstKeyGreaterOrEqual(in TKey key)
  {
    var iterator = GetFrozenLeafNode(key).CreateFrozenIterator();
    return iterator.SeekFirstKeyGreaterOrEqual(Comparer, in key);
  }

  public NodeIterator GetFirstIterator()
  {
    var topLevelLocker = TopLevelLocker;
    try
    {
      topLevelLocker.ReadLock();
      return FirstLeafNode.GetIterator(this);
    }
    finally
    {
      topLevelLocker.ReadUnlock();
    }
  }

  public FrozenNodeIterator GetFrozenFirstIterator()
  {
    return FirstLeafNode.CreateFrozenIterator();
  }

  public NodeIterator GetLastIterator()
  {
    var topLevelLocker = TopLevelLocker;
    try
    {
      topLevelLocker.ReadLock();
      return LastLeafNode.GetIterator(this);
    }
    finally
    {
      topLevelLocker.ReadUnlock();
    }
  }

  public FrozenNodeIterator GetFrozenLastIterator()
  {
    return LastLeafNode.CreateFrozenIterator();
  }

  public void SetNextOpIndex(long nextId)
      => OpIndexProvider.SetNextId(nextId);

  public long LastOpIndex => OpIndexProvider.LastId;
}
