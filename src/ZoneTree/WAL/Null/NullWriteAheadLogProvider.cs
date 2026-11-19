using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

public sealed class NullWriteAheadLogProvider : IWriteAheadLogProvider
{
  public IWriteAheadLog<TKey, TValue> GetOrCreateWal<TKey, TValue>(
      long segmentId,
      string category,
      WriteAheadLogOptions options,
      ISerializer<TKey> keySerializer,
      ISerializer<TValue> valueSerializer)
  {
    return new NullWriteAheadLog<TKey, TValue>();
  }

  public IWriteAheadLog<TKey, TValue> GetWal<TKey, TValue>(long segmentId, string category)
  {
    return new NullWriteAheadLog<TKey, TValue>();
  }

  public bool RemoveWal(long segmentId, string category)
  {
    return false;
  }

  public void DropStore()
  {
    // Nothing to drop
  }

  public void InitCategory(string category)
  {
    // Nothing to init
  }
}
