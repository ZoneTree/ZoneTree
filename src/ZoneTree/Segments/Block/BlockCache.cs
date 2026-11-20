using System.Collections.Concurrent;
using ZoneTree.Logging;

namespace ZoneTree.Segments.Block;

public sealed class BlockCache
{
  public readonly ConcurrentDictionary<int, DecompressedBlock> Table = new();

  public int Count => Table.Count;

  public void AddBlock(DecompressedBlock block)
  {
    Table.TryAdd(block.BlockIndex, block);
  }

  public void RemoveBlock(int blockIndex)
  {
    Table.TryRemove(blockIndex, out _);
  }

  public bool TryGetBlock(int blockIndex, out DecompressedBlock block)
  {
    return Table.TryGetValue(blockIndex, out block);
  }

  public void Clear()
  {
    Table.Clear();
  }

  public int RemoveBlocksAccessedBefore(long ticks)
  {
    var values = Table.Values.ToArray();
    if (values.Length < 1) return 0;
    var len = values.Length;
    var c = 0;
    for (var i = 0; i < len; i++)
    {
      var value = values[i];
      if (value.LastAccessTicks < ticks)
      {
        Table.TryRemove(value.BlockIndex, out _);
        ++c;
      }
    }
    return c;
  }
}

