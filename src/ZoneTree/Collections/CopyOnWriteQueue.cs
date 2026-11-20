using System.Collections;

namespace ZoneTree.Collections;

/// <summary>
/// A queue of ZoneTree's segments that lookups walk from the newest to the oldest as one array that no writer
/// changes. Items are enqueued and dequeued in FIFO order.
/// </summary>
/// <remarks>
/// <para>
/// Every enqueue and dequeue publishes a new array, newest first. A reader takes one reference and loops over it,
/// with no lock, version check or retry, and sees every item of that moment in order. Changes are rare, a segment
/// that moves forward or a merge that finishes, and each copies an array of a few segments. Reads run on every
/// lookup.
/// </para>
/// <para>
/// Any number of threads can read and change it. Changes that run at the same time retry, so none is lost.
/// </para>
/// </remarks>
/// <typeparam name="TItem">The type of the items.</typeparam>
public sealed class CopyOnWriteQueue<TItem> : IEnumerable<TItem>
    where TItem : class
{
  // Newest first. An array is never changed after it is published.
  TItem[] items = [];

  /// <summary>
  /// Creates an empty queue.
  /// </summary>
  public CopyOnWriteQueue()
  {
  }

  /// <summary>
  /// Creates a queue of the items in one copy, without enqueuing them one by one.
  /// </summary>
  /// <param name="newestFirst">The items from the newest to the oldest, the order the queue walks them.</param>
  public CopyOnWriteQueue(IEnumerable<TItem> newestFirst)
  {
    items = newestFirst.ToArray();
  }

  /// <summary>
  /// The items from the newest to the oldest, as they are at the moment of the read.
  /// </summary>
  public ReadOnlySpan<TItem> Items => Volatile.Read(ref items);

  /// <summary>
  /// The number of items.
  /// </summary>
  public int Length => Volatile.Read(ref items).Length;

  /// <summary>
  /// Whether the queue has no items.
  /// </summary>
  public bool IsEmpty => Volatile.Read(ref items).Length == 0;

  /// <summary>
  /// Adds the item as the newest.
  /// </summary>
  /// <param name="item">The item.</param>
  public void Enqueue(TItem item)
  {
    while (true)
    {
      var current = Volatile.Read(ref items);
      var next = new TItem[current.Length + 1];
      next[0] = item;
      Array.Copy(current, 0, next, 1, current.Length);
      if (Interlocked.CompareExchange(ref items, next, current) == current)
        return;
    }
  }

  /// <summary>
  /// Removes the oldest item.
  /// </summary>
  /// <param name="item">The oldest item, or null if the queue is empty.</param>
  /// <returns>true if an item was removed; false if the queue is empty.</returns>
  public bool TryDequeue(out TItem item)
  {
    while (true)
    {
      var current = Volatile.Read(ref items);
      var length = current.Length;
      if (length == 0)
      {
        item = null;
        return false;
      }
      var next = length == 1 ? [] : new TItem[length - 1];
      Array.Copy(current, next, length - 1);
      if (Interlocked.CompareExchange(ref items, next, current) == current)
      {
        item = current[length - 1];
        return true;
      }
    }
  }

  /// <summary>
  /// Returns the items from the newest to the oldest, as they are at the moment of the call. It returns the
  /// queue's own array without copying it, so the caller must not change it.
  /// </summary>
  public TItem[] ToLastInFirstArray() => Volatile.Read(ref items);

  /// <summary>
  /// Walks the items from the newest to the oldest, as they are when the walk starts.
  /// </summary>
  public ReadOnlySpan<TItem>.Enumerator GetEnumerator() => Items.GetEnumerator();

  IEnumerator<TItem> IEnumerable<TItem>.GetEnumerator() =>
      ((IEnumerable<TItem>)Volatile.Read(ref items)).GetEnumerator();

  IEnumerator IEnumerable.GetEnumerator() => Volatile.Read(ref items).GetEnumerator();
}
