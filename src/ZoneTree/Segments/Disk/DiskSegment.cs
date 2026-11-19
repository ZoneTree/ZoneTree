using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ZoneTree.Collections;
using ZoneTree.Comparers;
using ZoneTree.Exceptions;
using ZoneTree.Hashers;
using ZoneTree.Options;
using ZoneTree.Segments.Block;
using ZoneTree.Segments.Model;
using ZoneTree.Segments.RandomAccess;
using ZoneTree.Serializers;
using ZoneTree.Synchronization;

namespace ZoneTree.Segments.Disk;

[StructLayout(LayoutKind.Explicit, Size = 64)]
struct ReadCounterStripe
{
  [FieldOffset(0)]
  public int Value;
}

public abstract class DiskSegment<TKey, TValue> : IDiskSegment<TKey, TValue>
{
  static readonly int ReadCounterStripeCount = (int)BitOperations.RoundUpToPowerOf2(
      (uint)Math.Min(Environment.ProcessorCount, 64));

  static readonly int ReadCounterStripeMask = ReadCounterStripeCount - 1;

  sealed class SearchHint
  {
    public readonly BlockPin BlockPin = new()
    {
      ContributeToTheBlockCache = true
    };

    public long LastIndex = -1;

    public int Direction;

    public TKey[] PrefetchedKeys;

    public TValue[] PrefetchedValues;

    public long PrefetchStartIndex;

    public int PrefetchCount;

    public int PrefetchPosition;

    public void EnsurePrefetchBuffers(int prefetchSize)
    {
      PrefetchedKeys ??= new TKey[prefetchSize];
      PrefetchedValues ??= new TValue[prefetchSize];
    }
  }

  public long SegmentId { get; }

  readonly IRefComparer<TKey> Comparer;

  protected readonly ISerializer<TKey> KeySerializer;

  protected readonly ISerializer<TValue> ValueSerializer;

  protected readonly int MaterializedEntryCacheSize;

  readonly int SearchHintPrefetchSize;

  protected IRandomAccessDevice DataDevice;

  protected int KeySize;

  protected int ValueSize;

  protected IReadOnlyList<SparseArrayEntry<TKey, TValue>> SparseArray = Array.Empty<SparseArrayEntry<TKey, TValue>>();

  ReadEntriesDelegate<TKey, TValue> ReadEntriesHandler;

  sealed class DenseKeyRange(
      IReadOnlyList<SparseArrayEntry<TKey, TValue>> entries, long firstKey, int direction)
  {
    public readonly IReadOnlyList<SparseArrayEntry<TKey, TValue>> Entries = entries;
    public readonly long FirstKey = firstKey;
    public readonly int Direction = direction;
  }

  volatile DenseKeyRange DenseKeys;

  DenseKeyRange GetDenseKeyRange(IReadOnlyList<SparseArrayEntry<TKey, TValue>> entries)
  {
    if (entries.Count == 0 || entries[0].Index != 0 || entries[^1].Index != Length - 1)
      return new(entries, 0, 0);
    var ascending = Comparer is Int64ComparerAscending or Int32ComparerAscending;
    var descending = Comparer is Int64ComparerDescending or Int32ComparerDescending;
    if (!ascending && !descending)
      return new(entries, 0, 0);
    var first = GetIntegerKey(in entries[0].Key);
    var last = GetIntegerKey(in entries[^1].Key);
    var distance = descending ? unchecked((ulong)(first - last)) : unchecked((ulong)(last - first));
    // Ordered, unique integer keys fill the entire interval exactly when
    // its width matches the number of entries. No per-key map is needed.
    return new(entries, first, distance == (ulong)(Length - 1) ? (descending ? -1 : 1) : 0);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  static long GetIntegerKey(in TKey key) => typeof(TKey) == typeof(long)
      ? Unsafe.As<TKey, long>(ref Unsafe.AsRef(in key))
      : Unsafe.As<TKey, int>(ref Unsafe.AsRef(in key));

  bool TryGetDenseKeyPosition(in TKey key, out long position)
  {
    if (typeof(TKey) != typeof(long) && typeof(TKey) != typeof(int))
    {
      position = 0;
      return false;
    }
    var entries = SparseArray;
    var range = DenseKeys;
    if (range == null || !ReferenceEquals(range.Entries, entries))
    {
      range = GetDenseKeyRange(entries);
      DenseKeys = range;
    }
    if (range.Direction == 0)
    {
      position = 0;
      return false;
    }
    var integerKey = GetIntegerKey(in key);
    var offset = range.Direction < 0
        ? unchecked((ulong)(range.FirstKey - integerKey))
        : unchecked((ulong)(integerKey - range.FirstKey));
    if (offset < (ulong)Length)
      position = (long)offset;
    else
      position = (range.Direction < 0 ? integerKey > range.FirstKey : integerKey < range.FirstKey)
          ? -1 : Length;
    return true;
  }

  LifecycleLeaseState IteratorLeaseState;

  /// <summary>
  /// Gets the number of active iterator leases.
  /// </summary>
  public long IteratorReaderCount => IteratorLeaseState.LeaseCount;

  readonly ReadCounterStripe[] ReadCounterStripes =
      new ReadCounterStripe[ReadCounterStripeCount];

  protected volatile bool IsDropping;

  readonly Lock DropLock = new();

  readonly ThreadLocal<SearchHint> SearchHints;

  int AreSearchHintsDisposed;

  public long Length { get; protected set; }

  public long MaximumOpIndex => 0;

  public bool IsFullyFrozen => true;

  public bool IsIterativeIndexReader => false;

  public abstract int ReadBufferCount { get; }

  public Action<IDiskSegment<TKey, TValue>, Exception> DropFailureReporter { get; set; }

  public CircularCache<TKey> CircularKeyCache { get; }

  public CircularCache<TValue> CircularValueCache { get; }

  protected int ActiveReadCount
  {
    get
    {
      var result = 0;
      var stripes = ReadCounterStripes;
      for (var i = 0; i < stripes.Length; ++i)
        result += Volatile.Read(ref stripes[i].Value);
      return result;
    }
  }

  protected ZoneTreeOptions<TKey, TValue> Options;

  protected DiskSegment(
      long segmentId,
      ZoneTreeOptions<TKey, TValue> options)
  {
    Options = options;
    SegmentId = segmentId;
    Comparer = options.Comparer;
    KeySerializer = options.KeySerializer;
    ValueSerializer = options.ValueSerializer;
    var diskOptions = options.DiskSegmentOptions;
    MaterializedEntryCacheSize = diskOptions.MaterializedEntryCacheSize;
    SearchHintPrefetchSize = diskOptions.SearchHintPrefetchSize;
    if (SearchHintPrefetchSize > 0)
      SearchHints = new(() => new());
    CircularKeyCache = new CircularCache<TKey>(
        diskOptions.KeyCacheSize,
        diskOptions.KeyCacheRecordLifeTimeInMillisecond);
    CircularValueCache = new CircularCache<TValue>(
        diskOptions.ValueCacheSize,
        diskOptions.ValueCacheRecordLifeTimeInMillisecond);
  }

  protected DiskSegment(long segmentId,
      ZoneTreeOptions<TKey, TValue> options,
      IRandomAccessDevice dataDevice)
  {
    Options = options;
    SegmentId = segmentId;
    DataDevice = dataDevice;

    Comparer = options.Comparer;
    KeySerializer = options.KeySerializer;
    ValueSerializer = options.ValueSerializer;
    var diskOptions = options.DiskSegmentOptions;
    MaterializedEntryCacheSize = diskOptions.MaterializedEntryCacheSize;
    SearchHintPrefetchSize = diskOptions.SearchHintPrefetchSize;
    if (SearchHintPrefetchSize > 0)
      SearchHints = new(() => new());
    CircularKeyCache = new CircularCache<TKey>(
        diskOptions.KeyCacheSize,
        diskOptions.KeyCacheRecordLifeTimeInMillisecond);
    CircularValueCache = new CircularCache<TValue>(
        diskOptions.ValueCacheSize,
        diskOptions.ValueCacheRecordLifeTimeInMillisecond);
  }

  public bool ContainsKey(in TKey key, ref KeyHashProvider<TKey> keyHashProvider)
  {
    if (TryGetDenseKeyPosition(in key, out var densePosition))
      return (ulong)densePosition < (ulong)Length;
    var sparseArrayLength = SparseArray.Count;
    long lower = 0;
    long upper = Length - 1;

    if (sparseArrayLength != 0)
    {
      (var index, var found) = SearchLastSmallerOrEqualPositionInSparseArray(in key);
      if (found)
        return true;
      if (index == -1 || index == sparseArrayLength - 1)
        return false;
      lower = SparseArray[index].Index;
      upper = SparseArray[index + 1].Index - 1;
    }
    var res = SearchKeys(in key, lower, upper);
    return res >= 0;
  }

  public TKey GetKey(long index)
  {
    return ReadKey(index);
  }

  public TValue GetValue(long index)
  {
    return ReadValue(index);
  }

  public bool TryGet(in TKey key, out TValue value, ref KeyHashProvider<TKey> keyHashProvider)
  {
    if (TryGetDenseKeyPosition(in key, out var densePosition))
    {
      if ((ulong)densePosition < (ulong)Length)
      {
        value = GetValue(densePosition);
        return true;
      }
      value = default;
      return false;
    }
    var searchHint = SearchHints?.Value;
    var direction = searchHint?.Direction ?? 0;
    if (direction != 0 && TryGetFromSearchHint(
        searchHint,
        direction,
        in key,
        out value))
    {
      return true;
    }

    var sparseArrayLength = SparseArray.Count;
    long lower = 0;
    long upper = Length - 1;

    if (sparseArrayLength != 0)
    {
      (var index, var found) = SearchLastSmallerOrEqualPositionInSparseArray(in key);
      if (found)
      {
        if (searchHint != null)
          UpdateSearchHint(searchHint, SparseArray[index].Index);
        value = SparseArray[index].Value;
        return true;
      }
      if (index == -1 || index == sparseArrayLength - 1)
      {
        if (searchHint != null)
          ResetSearchHint(searchHint);
        value = default;
        return false;
      }
      lower = SparseArray[index].Index;
      upper = SparseArray[index + 1].Index - 1;
    }
    var result = SearchKeys(in key, lower, upper);
    if (result < 0)
    {
      if (searchHint != null)
        ResetSearchHint(searchHint);
      value = default;
      return false;
    }
    if (searchHint != null)
      UpdateSearchHint(searchHint, result);
    value = GetValue(result);
    return true;
  }

  bool TryGetFromSearchHint(
      SearchHint searchHint,
      int direction,
      in TKey key,
      out TValue value)
  {
    var hintedIndex = searchHint.LastIndex + direction;
    if ((ulong)hintedIndex >= (ulong)Length)
    {
      InvalidateSearchHintPrefetch(searchHint);
      value = default;
      return false;
    }

    var position = searchHint.PrefetchPosition;
    if ((uint)position >= (uint)searchHint.PrefetchCount ||
        searchHint.PrefetchStartIndex + position != hintedIndex)
    {
      if (!PrefetchSearchHint(searchHint, hintedIndex, direction))
      {
        InvalidateSearchHintPrefetch(searchHint);
        value = default;
        return false;
      }
      position = searchHint.PrefetchPosition;
    }

    var hintedKey = searchHint.PrefetchedKeys[position];
    if (Comparer.Compare(in hintedKey, in key) != 0)
    {
      InvalidateSearchHintPrefetch(searchHint);
      value = default;
      return false;
    }

    value = searchHint.PrefetchedValues[position];
    searchHint.LastIndex = hintedIndex;
    searchHint.PrefetchPosition = position + direction;
    return true;
  }

  bool PrefetchSearchHint(
      SearchHint searchHint,
      long hintedIndex,
      int direction)
  {
    var prefetchSize = (int)Math.Min(SearchHintPrefetchSize, Length);
    long startIndex;
    int count;
    int position;
    if (direction > 0)
    {
      startIndex = hintedIndex;
      count = (int)Math.Min(prefetchSize, Length - startIndex);
      position = 0;
    }
    else
    {
      startIndex = Math.Max(0, hintedIndex - prefetchSize + 1);
      count = (int)(hintedIndex - startIndex + 1);
      position = count - 1;
    }

    searchHint.EnsurePrefetchBuffers(prefetchSize);
    int readCount;
    try
    {
      readCount = ReadEntries(
          startIndex,
          count,
          searchHint.PrefetchedKeys,
          searchHint.PrefetchedValues,
          0,
          searchHint.BlockPin);
    }
    finally
    {
      searchHint.BlockPin.Clear();
    }
    if (readCount != count)
      return false;

    searchHint.PrefetchStartIndex = startIndex;
    searchHint.PrefetchCount = readCount;
    searchHint.PrefetchPosition = position;
    return true;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  static void UpdateSearchHint(SearchHint searchHint, long index)
  {
    var lastIndex = searchHint.LastIndex;
    searchHint.Direction = lastIndex < 0
        ? 0
        : (index - lastIndex) switch
        {
          1 => 1,
          -1 => -1,
          _ => 0
        };
    searchHint.LastIndex = index;
    ClearSearchHintPrefetch(searchHint);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  static void ResetSearchHint(SearchHint searchHint)
  {
    searchHint.LastIndex = -1;
    searchHint.Direction = 0;
    ClearSearchHintPrefetch(searchHint);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  static void InvalidateSearchHintPrefetch(SearchHint searchHint)
  {
    searchHint.Direction = 0;
    ClearSearchHintPrefetch(searchHint);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  static void ClearSearchHintPrefetch(SearchHint searchHint)
  {
    searchHint.PrefetchCount = 0;
    searchHint.PrefetchPosition = 0;
    searchHint.BlockPin.Clear();
  }

  public void InitSparseArray(int size)
  {
    var len = Length;
    size = (int)Math.Min(size, len);
    if (size < 1)
    {
      SparseArray = Array.Empty<SparseArrayEntry<TKey, TValue>>();
      return;
    }
    var step = (int)(len / size);
    if (step < 1)
      return;
    var sparseArray = new List<SparseArrayEntry<TKey, TValue>>();
    var keys = new TKey[1];
    var values = new TValue[1];
    for (int i = 0; i < len; i += step)
    {
      var sparseArrayEntry = CreateSparseArrayEntry(i, keys, values);
      sparseArray.Add(sparseArrayEntry);
    }
    if (sparseArray[^1].Index != len - 1)
    {
      var sparseArrayEntry = CreateSparseArrayEntry(len - 1, keys, values);
      sparseArray.Add(sparseArrayEntry);
    }
    SparseArray = sparseArray;
  }

  public void LoadIntoMemory()
  {
    InitSparseArray((int)Math.Min(Length, int.MaxValue));
  }

  SparseArrayEntry<TKey, TValue> CreateSparseArrayEntry(
      long index,
      TKey[] keys,
      TValue[] values)
  {
    if (ReadEntries(index, 1, keys, values, 0, null) != 1)
      throw new InvalidOperationException(
          $"Could not read sparse-array entry at index {index}.");
    return new SparseArrayEntry<TKey, TValue>(keys[0], values[0], index);
  }

  protected TKey ReadKey(long index) => ReadKey(index, null);

  protected TValue ReadValue(long index) => ReadValue(index, null);

  // Return the complemented insertion position for a missing key so point
  // lookups and both seek directions can share the same search.
  protected virtual long SearchKeys(in TKey key, long lower, long upper)
      => SearchKeysIndividually(in key, lower, upper);

  long SearchKeysIndividually(in TKey key, long lower, long upper)
  {
    while (lower <= upper)
    {
      var mid = lower + (upper - lower) / 2;
      var comparison = Comparer.Compare(ReadKey(mid), in key);
      if (comparison == 0)
        return mid;
      if (comparison < 0)
        lower = mid + 1;
      else
        upper = mid - 1;
    }
    return ~lower;
  }

  protected long SearchFixedSizeKeys(
      in TKey key, long lower, long upper,
      IRandomAccessDevice device, int recordSize)
  {
    const int maximumWindowBytes = 64 * 1024;
    var count = upper - lower + 1;
    if (count <= 0)
      return ~lower;
    // Sparse arrays usually restrict the search to a small window. Keep
    // large searches incremental rather than reading an entire segment.
    if (count > maximumWindowBytes / recordSize)
      return SearchKeysIndividually(in key, lower, upper);
    var byteOffset = lower * recordSize;
    var byteCount = (int)(count - 1) * recordSize + KeySize;
    if (!IsWithinOneCompressionBlock(byteOffset, byteCount))
      return SearchKeysIndividually(in key, lower, upper);

    var readStripe = BeginRead();
    try
    {
      var bytes = device.GetBytes(byteOffset, byteCount);
      var start = lower;
      while (lower <= upper)
      {
        var mid = lower + (upper - lower) / 2;
        var candidate = KeySerializer.Deserialize(
            bytes.Slice((int)(mid - start) * recordSize, KeySize));
        var comparison = Comparer.Compare(in candidate, in key);
        if (comparison == 0)
          return mid;
        if (comparison < 0)
          lower = mid + 1;
        else
          upper = mid - 1;
      }
      return ~lower;
    }
    finally
    {
      EndRead(readStripe);
    }
  }

  protected unsafe long SearchVariableSizeKeys(
      in TKey key, long lower, long upper,
      IRandomAccessDevice headerDevice, int recordSize)
  {
    const int maximumWindowBytes = 64 * 1024;
    var count = upper - lower + 1;
    if (count <= 0)
      return ~lower;
    if (count > maximumWindowBytes / recordSize)
      return SearchKeysIndividually(in key, lower, upper);
    var headerOffset = lower * recordSize;
    var headerLength = (int)(count - 1) * recordSize + sizeof(KeyHead);
    if (!IsWithinOneCompressionBlock(headerOffset, headerLength))
      return SearchKeysIndividually(in key, lower, upper);

    var readStripe = BeginRead();
    try
    {
      var headers = headerDevice.GetBytes(headerOffset, headerLength);
      var first = BinarySerializerHelper.FromByteArray<KeyHead>(headers);
      var last = BinarySerializerHelper.FromByteArray<KeyHead>(headers, (int)(count - 1) * recordSize);
      var byteCount = last.KeyOffset + last.KeyLength - first.KeyOffset;
      // Variable-sized values may separate the keys by megabytes. Only fetch
      // the whole key window when it is small enough to be useful.
      var hasKeyWindow = byteCount >= 0 && byteCount <= maximumWindowBytes &&
          (byteCount == 0 || IsWithinOneCompressionBlock(first.KeyOffset, byteCount));
      var keyWindow = hasKeyWindow
          ? DataDevice.GetBytes(first.KeyOffset, (int)byteCount)
          : default;
      var start = lower;
      while (lower <= upper)
      {
        var mid = lower + (upper - lower) / 2;
        var head = BinarySerializerHelper.FromByteArray<KeyHead>(
            headers, (int)(mid - start) * recordSize);
        var bytes = hasKeyWindow
            ? keyWindow.Slice((int)(head.KeyOffset - first.KeyOffset), head.KeyLength)
            : DataDevice.GetBytes(head.KeyOffset, head.KeyLength);
        var candidate = KeySerializer.Deserialize(bytes);
        var comparison = Comparer.Compare(in candidate, in key);
        if (comparison == 0)
          return mid;
        if (comparison < 0)
          lower = mid + 1;
        else
          upper = mid - 1;
      }
      return ~lower;
    }
    finally
    {
      EndRead(readStripe);
    }
  }

  bool IsWithinOneCompressionBlock(long offset, long length)
  {
    var blockSize = Options.DiskSegmentOptions.CompressionBlockSize;
    return offset / blockSize == (offset + length - 1) / blockSize;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  protected int BeginRead()
  {
    var stripeIndex = Thread.GetCurrentProcessorId() & ReadCounterStripeMask;
    Interlocked.Increment(ref ReadCounterStripes[stripeIndex].Value);
    if (!IsDropping)
      return stripeIndex;

    Interlocked.Decrement(ref ReadCounterStripes[stripeIndex].Value);
    throw new DiskSegmentIsDroppingException();
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  protected void EndRead(int stripeIndex)
  {
    Interlocked.Decrement(ref ReadCounterStripes[stripeIndex].Value);
  }

  protected abstract TKey ReadKey(long index, BlockPin blockPin);

  protected abstract TValue ReadValue(long index, BlockPin blockPin);

  /// <summary>
  /// Finds the position of element that is greater or equal than key.
  /// </summary>
  /// <param name="key">The key</param>
  /// <returns>The length of the sparse array or a valid position</returns>
  int FindFirstGreaterOrEqualPositionInSparseArray(TKey key)
  {
    var list = SparseArray;
    var comp = Comparer;
    var left = 0;
    var right = list.Count - 1;
    var result = list.Count;
    while (left <= right)
    {
      var mid = left + (right - left) / 2;
      if (comp.Compare(in list[mid].Key, in key) >= 0)
      {
        result = mid;
        right = mid - 1;
      }
      else
        left = mid + 1;
    }
    return result;
  }

  /// <summary>
  /// Finds the position of element that is smaller or equal than key.
  /// </summary>
  /// <param name="key">The key</param>
  /// <returns>-1 or a valid position</returns>
  int FindLastSmallerOrEqualPositionInSparseArray(TKey key)
  {
    var list = SparseArray;
    var comp = Comparer;
    var left = 0;
    var right = list.Count - 1;
    var result = -1;
    while (left <= right)
    {
      var mid = left + (right - left) / 2;
      if (comp.Compare(in list[mid].Key, in key) <= 0)
      {
        result = mid;
        left = mid + 1;
      }
      else
        right = mid - 1;
    }
    return result;
  }

  (int index, bool found) SearchLastSmallerOrEqualPositionInSparseArray(in TKey key)
  {
    var list = SparseArray;
    var len = list.Count;
    if (len == 0)
      return (-1, false);

    var position = FindLastSmallerOrEqualPositionInSparseArray(key);
    if (position == -1)
      return (-1, false);
    var exactMatch = Comparer.Compare(SparseArray[position].Key, key) == 0;
    return (position, exactMatch);
  }

  (int index, bool found) SearchFirstGreaterOrEqualPositionInSparseArray(in TKey key)
  {
    var list = SparseArray;
    var len = list.Count;
    if (len == 0)
      return (0, false);

    var position = FindFirstGreaterOrEqualPositionInSparseArray(key);
    if (position == len)
      return (len, false);
    var exactMatch = Comparer.Compare(SparseArray[position].Key, key) == 0;
    return (position, exactMatch);
  }

  public TKey[] GetFirstKeysOfEveryPart()
  {
    return Array.Empty<TKey>();
  }

  public TKey[] GetLastKeysOfEveryPart()
  {
    return Array.Empty<TKey>();
  }

  public TValue[] GetLastValuesOfEveryPart()
  {
    return Array.Empty<TValue>();
  }

  public IDiskSegment<TKey, TValue> GetPart(int partIndex)
  {
    return null;
  }

  public void Dispose()
  {
    Dispose(true);
    GC.SuppressFinalize(this);
  }

  protected virtual void Dispose(bool disposing)
  {
    if (!disposing)
      return;
    DisposeSearchHints();
    ReleaseResources();
  }

  public void Drop()
  {
    lock (DropLock)
    {
      // Request retirement and prevent new iterators from attaching.
      // Complete the drop immediately when no iterators are active;
      // otherwise, the final iterator completes it when detached.
      if (IteratorLeaseState.RequestRetirement())
        CompleteDrop(reportFailure: false);
    }
  }

  protected abstract void DeleteDevices();

  public IIndexedReader<TKey, TValue> GetIndexedReader()
  {
    return this;
  }

  public void AttachIterator()
  {
    if (!IteratorLeaseState.TryAcquire())
      throw new InvalidOperationException(
          "Cannot attach an iterator after disk segment retirement has started.");
  }

  public void DetachIterator()
  {
    if (IteratorLeaseState.Release())
    {
      lock (DropLock)
      {
        if (IteratorLeaseState.TryBeginRetirementCompletion())
          CompleteDrop(reportFailure: true);
      }
    }
  }

  void CompleteDrop(bool reportFailure)
  {
    try
    {
      // reads will increase ReadCount when they begin,
      // and decrease ReadCount when they end.
      IsDropping = true;
      // After the flag change,
      // reads will start throwing DiskSegmentIsDroppingException

      // Delay the drop operation until all reads finalized
      // either with success or exception.
      if (ActiveReadCount != 0)
      {
        // Synchronize reads with drop operation.
        SpinWait.SpinUntil(() => ActiveReadCount == 0);
      }

      // No active reads remaining.
      // Safe to drop.

      DeleteDevices();
      IteratorLeaseState.CompleteRetirement();
    }
    catch (Exception e)
    {
      IteratorLeaseState.CancelRetirementCompletion();
      if (!reportFailure)
        throw;
      DropFailureReporter?.Invoke(this, e);
    }
  }

  public ISeekableIterator<TKey, TValue> GetSeekableIterator(bool contributeToTheBlockCache)
  {
    return GetSeekableIterator(
        contributeToTheBlockCache,
        IteratorDefaultValues.DiskSegmentPrefetchSize);
  }

  public ISeekableIterator<TKey, TValue> GetSeekableIterator(
      bool contributeToTheBlockCache,
      int prefetchSize)
  {
    if (prefetchSize < 2)
      return new SeekableIterator<TKey, TValue>(this, contributeToTheBlockCache);

    // Create the delegate once per segment instead of once per iterator.
    var readEntries = ReadEntriesHandler ??= ReadEntries;
    return new PrefetchingSeekableIterator<TKey, TValue>(
          this,
          readEntries,
          prefetchSize,
          contributeToTheBlockCache);
  }

  public long GetLastSmallerOrEqualPosition(in TKey key)
  {
    if (TryGetDenseKeyPosition(in key, out var densePosition))
      return Math.Min(densePosition, Length - 1);
    var sparseArrayLength = SparseArray.Count;
    long lower = 0;
    long upper = Length - 1;
    if (sparseArrayLength != 0)
    {
      (var index, var found) = SearchLastSmallerOrEqualPositionInSparseArray(in key);
      if (found)
        return SparseArray[index].Index;
      if (index == -1)
        return -1;
      if (index == sparseArrayLength - 1)
        return SparseArray[index].Index;
      lower = SparseArray[index].Index;
      upper = SparseArray[index + 1].Index - 1;
    }
    var result = SearchKeys(in key, lower, upper);
    return result >= 0 ? result : ~result - 1;
  }

  public long GetFirstGreaterOrEqualPosition(in TKey key)
  {
    if (TryGetDenseKeyPosition(in key, out var densePosition))
      return Math.Max(densePosition, 0);
    var sparseArrayLength = SparseArray.Count;
    long lower = 0;
    long upper = Length - 1;
    if (sparseArrayLength != 0)
    {
      (var index, var found) = SearchFirstGreaterOrEqualPositionInSparseArray(in key);
      if (found)
        return SparseArray[index].Index;
      if (index == sparseArrayLength)
        return Length;
      if (index == 0)
        return SparseArray[index].Index;
      if (index > 0)
        --index;
      lower = SparseArray[index].Index;
      upper = SparseArray[index + 1].Index - 1;
    }
    var result = SearchKeys(in key, lower, upper);
    return result >= 0 ? result : ~result;
  }

  public virtual void ReleaseResources()
  {
    DisposeSearchHints();
    DataDevice?.Dispose();
  }

  void DisposeSearchHints()
  {
    var searchHints = SearchHints;
    if (searchHints != null &&
        Interlocked.Exchange(ref AreSearchHintsDisposed, 1) == 0)
      searchHints.Dispose();
  }

  public abstract int ReleaseReadBuffers(long ticks);

  public void Drop(HashSet<long> excludedPartIds)
  {
    if (excludedPartIds.Contains(SegmentId))
      return;
    Drop();
  }

  public bool IsBeginningOfAPart(long index) => false;

  public bool IsEndOfAPart(long index) => false;

  public int GetPartIndex(long index) => -1;

  public int GetPartCount() => 0;

  public DiskSegmentFile[] GetFiles()
  {
    var files = new List<DiskSegmentFile>();
    AddFileIfExists(files, DiskSegmentConstants.DataHeaderCategory);
    AddFileIfExists(files, DiskSegmentConstants.DataCategory);
    AddFileIfExists(files, DiskSegmentConstants.SparseArrayCategory);
    return [.. files];
  }

  void AddFileIfExists(
      List<DiskSegmentFile> files,
      string category)
  {
    AddFileIfExists(files, category, false);
    AddFileIfExists(files, category, true);
  }

  void AddFileIfExists(
      List<DiskSegmentFile> files,
      string category,
      bool isCompressed)
  {
    var deviceManager = Options.RandomAccessDeviceManager;
    if (!deviceManager.DeviceExists(SegmentId, category, isCompressed))
      return;

    var path = deviceManager.GetFilePath(SegmentId, category);
    if (isCompressed)
      path += ".z";

    files.Add(new DiskSegmentFile(
        SegmentId,
        path,
        System.IO.Path.GetFileName(path),
        Length));
  }

  abstract public void SetDefaultSparseArray(IReadOnlyList<SparseArrayEntry<TKey, TValue>> defaultSparseArray);

  public int ReleaseCircularKeyCacheRecords()
  {
    return CircularKeyCache.ReleaseInactiveCacheRecords();
  }

  public int ReleaseCircularValueCacheRecords()
  {
    return CircularValueCache.ReleaseInactiveCacheRecords();
  }

  public TKey GetKey(long index, BlockPin blockPin)
  {
    return ReadKey(index, blockPin);
  }

  public TValue GetValue(long index, BlockPin blockPin)
  {
    return ReadValue(index, blockPin);
  }

  public abstract int ReadEntries(
      long startIndex,
      int count,
      TKey[] keys,
      TValue[] values,
      int destinationIndex,
      BlockPin blockPin);
}
