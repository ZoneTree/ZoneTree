using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ZoneTree.Segments.Block;

public sealed class MaterializedEntryCache<TKey, TValue>
{
  const int ChunkSize = 16;

  readonly MaterializedEntryChunk<TKey, TValue>[] Slots;

  // Slots.Length - 1 for power-of-two sizes, otherwise -1.
  readonly int SlotMask;

  MaterializedEntryCache(int cacheSize)
  {
    Slots = new MaterializedEntryChunk<TKey, TValue>[cacheSize];
    SlotMask = BitOperations.IsPow2((uint)cacheSize) ? cacheSize - 1 : -1;
  }

  public static MaterializedEntryCache<TKey, TValue> GetOrCreate(
      DecompressedBlock block,
      int cacheSize)
  {
    if (block == null || cacheSize <= 0)
      return null;

    var entries = Volatile.Read(ref block.MaterializedEntries);
    if (entries is MaterializedEntryCache<TKey, TValue> cache)
      return cache;
    if (entries != null)
      return null;

    cache = new MaterializedEntryCache<TKey, TValue>(cacheSize);
    entries = Interlocked.CompareExchange(
        ref block.MaterializedEntries,
        cache,
        null);
    return entries == null
        ? cache
        : entries as MaterializedEntryCache<TKey, TValue>;
  }

  public bool TryCopy(
      long startIndex,
      int count,
      TKey[] keys,
      TValue[] values,
      int destinationIndex)
  {
    if (count <= 0)
      return false;

    var currentIndex = startIndex;
    var remaining = count;
    while (remaining != 0)
    {
      var chunkStartIndex = GetChunkStartIndex(currentIndex);
      var chunkOffset = (int)(currentIndex - chunkStartIndex);
      var copyCount = Math.Min(ChunkSize - chunkOffset, remaining);
      var chunkIndex = chunkStartIndex / ChunkSize;
      var chunk = Volatile.Read(ref Slots[GetSlot(chunkIndex)]);
      if (chunk == null ||
          chunk.StartIndex != chunkStartIndex ||
          !chunk.TryCopy(
              currentIndex,
              copyCount,
              keys,
              values,
              destinationIndex))
      {
        return false;
      }

      currentIndex += copyCount;
      destinationIndex += copyCount;
      remaining -= copyCount;
    }
    return true;
  }

  public void Add(
      long startIndex,
      int count,
      TKey[] keys,
      TValue[] values,
      int sourceIndex)
  {
    // Single-record reads are used while creating sparse arrays. Caching them
    // would populate many mostly empty chunks with little chance of reuse.
    if (count <= 1)
      return;

    var currentIndex = startIndex;
    var remaining = count;
    while (remaining != 0)
    {
      var chunkStartIndex = GetChunkStartIndex(currentIndex);
      var chunkOffset = (int)(currentIndex - chunkStartIndex);
      var copyCount = Math.Min(ChunkSize - chunkOffset, remaining);
      var chunkIndex = chunkStartIndex / ChunkSize;
      var slot = GetSlot(chunkIndex);
      var chunk = Volatile.Read(ref Slots[slot]);
      if (chunk == null ||
          chunk.StartIndex != chunkStartIndex ||
          !chunk.Contains(currentIndex, copyCount))
      {
        var copiedKeys = new TKey[ChunkSize];
        var copiedValues = new TValue[ChunkSize];
        ushort validEntries = 0;
        if (chunk != null && chunk.StartIndex == chunkStartIndex)
        {
          Array.Copy(chunk.Keys, copiedKeys, ChunkSize);
          Array.Copy(chunk.Values, copiedValues, ChunkSize);
          validEntries = chunk.ValidEntries;
        }

        Array.Copy(keys, sourceIndex, copiedKeys, chunkOffset, copyCount);
        Array.Copy(values, sourceIndex, copiedValues, chunkOffset, copyCount);
        validEntries |= MaterializedEntryChunk<TKey, TValue>
            .GetEntryMask(chunkOffset, copyCount);

        var newChunk = new MaterializedEntryChunk<TKey, TValue>(
            chunkStartIndex,
            validEntries,
            copiedKeys,
            copiedValues);
        Volatile.Write(ref Slots[slot], newChunk);
      }

      currentIndex += copyCount;
      sourceIndex += copyCount;
      remaining -= copyCount;
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  static long GetChunkStartIndex(long index)
  {
    return index / ChunkSize * ChunkSize;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  int GetSlot(long chunkIndex)
  {
    // A block holds a contiguous range of entries, so mapping consecutive
    // chunks to consecutive slots keeps a whole block without collisions
    // when the cache has at least as many slots as the block has chunks.
    if (SlotMask >= 0)
      return (int)(chunkIndex & SlotMask);
    return (int)((ulong)chunkIndex % (ulong)Slots.Length);
  }
}

public sealed class MaterializedEntryChunk<TKey, TValue>(
    long startIndex,
    ushort validEntries,
    TKey[] keys,
    TValue[] values)
{
  public readonly long StartIndex = startIndex;

  public readonly ushort ValidEntries = validEntries;

  public readonly TKey[] Keys = keys;

  public readonly TValue[] Values = values;

  public bool Contains(long startIndex, int count)
  {
    var offset = startIndex - StartIndex;
    if (offset < 0 || offset > 15 || count > 16 - offset)
      return false;
    var entryMask = GetEntryMask((int)offset, count);
    return (ValidEntries & entryMask) == entryMask;
  }

  public bool TryCopy(
      long startIndex,
      int count,
      TKey[] keys,
      TValue[] values,
      int destinationIndex)
  {
    var offset = startIndex - StartIndex;
    if (offset < 0 || offset > 15 || count > 16 - offset)
      return false;

    var sourceIndex = (int)offset;
    var entryMask = GetEntryMask(sourceIndex, count);
    if ((ValidEntries & entryMask) != entryMask)
      return false;

    Array.Copy(Keys, sourceIndex, keys, destinationIndex, count);
    Array.Copy(Values, sourceIndex, values, destinationIndex, count);
    return true;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static ushort GetEntryMask(int offset, int count)
  {
    return (ushort)(((1U << count) - 1U) << offset);
  }
}
