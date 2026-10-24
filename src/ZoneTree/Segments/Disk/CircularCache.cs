using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ZoneTree.Segments.Disk;

public sealed class CircularCache<TDataType>
{
  const ulong HashMultiplier = 0x9E3779B97F4A7C15UL;

  const long EmptyIndex = -1;

  // Records are stored inline in the slots, so adding one allocates nothing
  // and a lookup reads a single slot instead of following a reference.
  // A slot's version is odd while a writer updates it and grows by two with
  // every completed update. A reader that sees the same even version before
  // and after copying a record knows the copy is consistent. Version 0 marks
  // a slot that has never been written.
  struct Slot
  {
    public int Version;
    public long Index;
    public long LastAccess;
    public TDataType Record;
  }

  readonly int CacheSize;

  readonly int HashShift;

  // Allocated on the first add, so unused caches cost no memory.
  Slot[] Slots;

  public int RecordLifeTimeInMillisecond { get; set; } = 10000;

  public CircularCache(int cacheSize, int recordLifeTimeInMillisecond)
  {
    RecordLifeTimeInMillisecond = recordLifeTimeInMillisecond;
    CacheSize = cacheSize;
    HashShift = GetHashShift(cacheSize);
  }

  public bool TryGet(long index, out TDataType key)
  {
    var slots = Volatile.Read(ref Slots);
    if (slots != null)
    {
      ref var slot = ref slots[GetSlotIndex(index)];
      var version = Volatile.Read(ref slot.Version);
      if (version != 0 && (version & 1) == 0 &&
          Volatile.Read(ref slot.Index) == index)
      {
        key = slot.Record;
        // Complete the record read before checking the version again.
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref slot.Version) == version)
        {
          // Avoid writing the shared slot on every hit.
          var now = Environment.TickCount64;
          if (slot.LastAccess != now)
            slot.LastAccess = now;
          return true;
        }
      }
    }
    key = default;
    return false;
  }

  public bool TryAdd(long index, ref TDataType key)
  {
    if (CacheSize < 1) return false;
    var slots = Volatile.Read(ref Slots) ?? CreateSlots();
    ref var slot = ref slots[GetSlotIndex(index)];
    // A cache may drop records, so skip the add if another writer holds
    // the slot instead of waiting for it.
    if (!TryLockSlot(ref slot, out var version)) return false;
    slot.Index = index;
    slot.Record = key;
    slot.LastAccess = Environment.TickCount64;
    Volatile.Write(ref slot.Version, unchecked(version + 2));
    return true;
  }

  public int ReleaseInactiveCacheRecords()
  {
    var slots = Volatile.Read(ref Slots);
    if (slots == null) return 0;
    var ticks = Environment.TickCount64 - RecordLifeTimeInMillisecond;
    var len = slots.Length;
    var totalReleasedRecords = 0;
    for (var i = 0; i < len; ++i)
    {
      ref var slot = ref slots[i];
      var version = Volatile.Read(ref slot.Version);
      if (version == 0 || (version & 1) != 0 ||
          Volatile.Read(ref slot.Index) == EmptyIndex ||
          slot.LastAccess >= ticks)
        continue;
      // Any update after the checks above changes the version and fails
      // the exchange, so a locked slot still holds the record checked above.
      if (Interlocked.CompareExchange(
          ref slot.Version, unchecked(version + 1), version) != version)
        continue;
      slot.Index = EmptyIndex;
      slot.Record = default;
      Volatile.Write(ref slot.Version, unchecked(version + 2));
      ++totalReleasedRecords;
    }
    return totalReleasedRecords;
  }

  public void Clear()
  {
    // Readers still holding the old slots may return records added before
    // the clear, as they could before it started.
    Volatile.Write(ref Slots, null);
  }

  Slot[] CreateSlots()
  {
    var slots = new Slot[CacheSize];
    return Interlocked.CompareExchange(ref Slots, slots, null) ?? slots;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  static bool TryLockSlot(ref Slot slot, out int version)
  {
    version = Volatile.Read(ref slot.Version);
    return (version & 1) == 0 &&
        Interlocked.CompareExchange(
            ref slot.Version, unchecked(version + 1), version) == version;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  int GetSlotIndex(long index)
  {
    var hash = unchecked((ulong)index * HashMultiplier);
    var hashShift = HashShift;
    if (hashShift != 0)
      return (int)(hash >> hashShift);
    if (CacheSize == 1)
      return 0;
    return (int)Math.BigMul(
        hash,
        (ulong)(uint)CacheSize,
        out _);
  }

  static int GetHashShift(int cacheSize)
  {
    return cacheSize > 1 && BitOperations.IsPow2((uint)cacheSize)
        ? 64 - BitOperations.Log2((uint)cacheSize)
        : 0;
  }
}
