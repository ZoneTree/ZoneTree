using ZoneTree.Comparers;

namespace ZoneTree.Collections;

public sealed class FixedSizeMinHeap<T>
{
  readonly T[] items;

  int count;

  readonly IRefComparer<T> comparer;

  public int Count => count;

  public int Capacity => items.Length;

  public T Min
  {
    get
    {
      if (count == 0)
      {
        throw new InvalidOperationException("No elements in the heap.");
      }
      return items[0];
    }
  }

  public FixedSizeMinHeap(int capacity, IRefComparer<T> comparer)
  {
    items = new T[capacity];
    this.comparer = comparer;
  }

  public void Clear()
  {
    count = 0;
  }

  /// <summary>
  /// Inserts the new element, maintaining the heap property.
  ///
  /// If the heap is full and the element is greater than the current min element, the
  ///     element replaces the min element and this function returns true. If the heap is
  ///     full otherwise, this function returns false without modifying the heap.
  /// </summary>
  /// <param name="item"></param>
  /// <returns>true if inserted</returns>
  public bool Insert(in T item)
  {
    if (count < items.Length)
    {
      // There is room. We can add it and then Min-heapify.
      items[count] = item;
      count++;
      HeapifyLastLeaf();
      return true;
    }
    else
    {
      // No more room. If the element is greater than the minimum element, it replaces
      // the head and the heap is reheapified. Otherwise, it is not inserted.
      if (comparer.Compare(item, items[0]) > 0)
      {
        items[0] = item;
        HeapifyRoot();
        return true;
      }

      return false;
    }
  }

  /// <summary>
  /// Replaces the minimum element in the heap with the given element, and restores
  /// the heap property.
  /// </summary>
  /// <param name="newItem"></param>
  public void ReplaceMin(T newItem)
  {
    items[0] = newItem;
    HeapifyRoot();
  }

  public void RemoveMin()
  {
    count--;

    if (count > 0)
    {
      items[0] = items[count];
      HeapifyRoot();
    }
  }

  void Swap(int i, int j)
  {
    (items[j], items[i]) = (items[i], items[j]);
  }

  void HeapifyRoot()
  {
    // We are heapifying from the head of the list.
    int i = 0;
    int n = count;

    while (i < n)
    {
      // Calculate the current child node indexes.
      int n0 = (i + 1) * 2 - 1;
      int n1 = n0 + 1;

      if (n0 < n && comparer.Compare(items[i], items[n0]) > 0)
      {
        // We have to select the bigger of the two subtrees, and float
        // the current element down. This maintains the Min-heap property.
        if (n1 < n && comparer.Compare(items[n0], items[n1]) > 0)
        {
          Swap(i, n1);
          i = n1;
        }
        else
        {
          Swap(i, n0);
          i = n0;
        }
      }
      else if (n1 < n && comparer.Compare(items[i], items[n1]) > 0)
      {
        // Float down the "right" subtree. We needn't compare this subtree
        // to the "left", because if the element was smaller than that, the
        // first if statement's predicate would have evaluated to true.
        Swap(i, n1);
        i = n1;
      }
      else
      {
        // Else, the current element is in its final position. Break out
        // of the current loop and return.
        break;
      }
    }
  }

  void HeapifyLastLeaf()
  {
    int i = count - 1;
    while (i > 0)
    {
      int j = (i + 1) / 2 - 1;

      if (comparer.Compare(items[i], items[j]) < 0)
      {
        Swap(i, j);
        i = j;
      }
      else
      {
        break;
      }
    }
  }

  public ReadOnlySpan<T> Items => new ReadOnlySpan<T>(items, 0, Count);
}
