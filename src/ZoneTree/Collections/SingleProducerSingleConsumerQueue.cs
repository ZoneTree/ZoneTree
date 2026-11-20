using System.Collections;

namespace ZoneTree.Collections;

/// <summary>
/// A queue of ZoneTree's segments.
/// 1. It is thread-safe for one producer and one consumer, and for any number of
///    readers that enumerate it.
/// 2. Enqueue takes a lock only when the queue is full, so inserts are almost lock-free.
/// 3. Dequeue takes a lock, but the producer almost never contends for it.
/// 4. Items are enqueued and dequeued in FIFO order, but enumerated in LIFO order,
///    so a lookup such as TryGetFromReadonlySegments sees the newest segment first.
///    Enqueue and dequeue order: i1, i2, i3, i4.
///    Enumeration order: i4, i3, i2, i1.
/// </summary>
/// <typeparam name="TQueueItem">Type of the queue item.</typeparam>
public sealed class SingleProducerSingleConsumerQueue<TQueueItem>
    : IEnumerable<TQueueItem>
    where TQueueItem : class
{
  sealed class QueueItemsChunk
  {
    const int ChunkSize = 16;

    /// <summary>
    /// Start of the queue inclusive.
    /// </summary>
    public volatile int Start;

    /// <summary>
    /// End of the queue exclusive.
    /// </summary>
    public volatile int End;

    public volatile TQueueItem[] Items;

    public bool IsEmpty => Start == End;

    public int ItemsCount
    {
      get
      {
        var size = Items.Length;
        return (End + size - Start) % size;
      }
    }

    public QueueItemsChunk()
    {
      Items = new TQueueItem[ChunkSize];
    }

    public QueueItemsChunk(TQueueItem[] items, int start, int end)
    {
      Items = items;
      Start = start;
      End = end;
    }

    public List<TQueueItem> ToFirstInFirstArray()
    {
      var items = Items;
      var size = items.Length;
      var end = End;
      var start = Start;
      var list = new List<TQueueItem>(ItemsCount);

      while (start != end)
      {
        list.Add(items[start]);
        start = (start + 1) % size;
      }
      return list;
    }

    /// <summary>
    /// Returns the items from the newest to the oldest. Start is read before End, so items that the producer
    /// enqueues during the walk are either in the range or newer than it. Each slot is read with acquire semantics,
    /// so the caller's later reads happen after the walk's reads. The queue checks for dequeues during the walk.
    /// </summary>
    public List<TQueueItem> ToLastInFirstArray()
    {
      var items = Items;
      var size = items.Length;
      var start = (size + Start - 1) % size;
      var end = (size + End - 1) % size;
      var list = new List<TQueueItem>(ItemsCount);

      while (start != end)
      {
        var item = Volatile.Read(ref items[end]);
        if (item != null)
          list.Add(item);
        end = (size + end - 1) % size;
      }
      return list;
    }
  }

  readonly Lock SyncRoot = new();

  // Odd while a dequeue runs, and grows by two with every dequeue. A dequeue during a newest-first walk lets the
  // producer wrap around past the Start the walk read, and the walk then skips items that stayed queued. A walk
  // that sees the same even version before and after had no dequeue during it. Ring positions repeat, so the walk
  // cannot check Start instead.
  long DequeueVersion;

  public int Length => Chunk.ItemsCount;

  public bool IsEmpty => Chunk.IsEmpty;

  volatile QueueItemsChunk Chunk = new();

  public SingleProducerSingleConsumerQueue()
  {
  }

  public SingleProducerSingleConsumerQueue(IEnumerable<TQueueItem> list)
  {
    foreach (var item in list)
    {
      Enqueue(item);
    }
  }

  /// <summary>
  /// Enqueue should not be called more than once at the same time.
  /// </summary>
  public void Enqueue(TQueueItem item)
  {
    var chunk = Chunk;
    var items = chunk.Items;
    var size = items.Length;
    var end = chunk.End;
    if ((end + 1) % size == chunk.Start)
    {
      // queue is full or was full.
      // lock frequency of enqueue is almost zero due to the exponential size increase.
      lock (SyncRoot)
      {
        var newItems = new TQueueItem[size * 2];
        Array.Copy(items, newItems, size);
        if (end < chunk.Start)
        {
          Array.Copy(items, 0, newItems, size, end);
          Array.Fill(newItems, null, 0, end);
          end = size + chunk.End;
        }
        chunk = Chunk = new QueueItemsChunk(newItems, chunk.Start, end);
        items = newItems;
      }
      size *= 2;
    }
    items[end] = item;
    chunk.End = (end + 1) % size;
  }

  /// <summary>
  /// TryDequeue should not be called more than once at the same time.
  /// </summary>
  public bool TryDequeue(out TQueueItem item)
  {
    var chunk = Chunk;
    var start = chunk.Start;
    var items = chunk.Items;
    var size = items.Length;
    item = items[start];
    if (item == null)
      return false;

    lock (SyncRoot)
    {
      if (!ReferenceEquals(chunk, Chunk))
      {
        chunk = Chunk;
        items = chunk.Items;
        size = items.Length;
      }
      // Full fences, so the walks see the odd version before any change below, and these changes before the even one.
      Interlocked.Increment(ref DequeueVersion);
      items[start] = null;
      chunk.Start = (start + 1) % size;
      Interlocked.Increment(ref DequeueVersion);
    }
    return true;
  }

  /// <summary>
  /// Returns the items from the newest to the oldest. It is safe while the producer and the consumer run, and it
  /// walks again if a dequeue ran during the walk.
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1002:Do not expose generic lists",
    Justification = "List<T> is intentionally exposed here to avoid extra allocation and preserve the concrete return type.")]
  public List<TQueueItem> ToLastInFirstArray()
  {
    var spinner = new SpinWait();
    while (true)
    {
      var version = Volatile.Read(ref DequeueVersion);
      if ((version & 1) == 0)
      {
        var list = Chunk.ToLastInFirstArray();
        if (Volatile.Read(ref DequeueVersion) == version)
          return list;
      }
      spinner.SpinOnce();
    }
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage(
      "Design",
      "CA1002:Do not expose generic lists",
      Justification = "List<T> is intentionally exposed here to avoid extra allocation and preserve the concrete return type.")]
  public List<TQueueItem> ToFirstInFirstArray() => Chunk.ToFirstInFirstArray();

  sealed class LastInFirstEnumerator : IEnumerator<TQueueItem>
  {
    TQueueItem current;

    public TQueueItem Current => current;

    object IEnumerator.Current => current;

    readonly QueueItemsChunk Chunk;

    TQueueItem[] Items;

    int Start;

    int End;

    int Size;

    public LastInFirstEnumerator(QueueItemsChunk chunk)
    {
      Chunk = chunk;
      Reset();
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// The enumeration of this quewue is LIFO.
    /// </summary>
    /// <returns></returns>
    public bool MoveNext()
    {
      do
      {
        if (Start == End)
          return false;
        current = Items[End];
        End = (Size + End - 1) % Size;
      }
      while (current == null);
      return true;
    }

    public void Reset()
    {
      var chunk = Chunk;
      Items = chunk.Items;
      Size = Items.Length;
      Start = (Size + chunk.Start - 1) % Size;
      End = (Size + chunk.End - 1) % Size;
    }
  }

  public IEnumerator<TQueueItem> GetEnumerator()
  {
    return new LastInFirstEnumerator(Chunk);
  }

  IEnumerator IEnumerable.GetEnumerator()
  {
    return new LastInFirstEnumerator(Chunk);
  }
}
