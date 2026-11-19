using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.WAL;

public interface IWriteAheadLogProvider
{
  void InitCategory(string category);

  IWriteAheadLog<TKey, TValue> GetOrCreateWal<TKey, TValue>(
      long segmentId,
      string category,
      WriteAheadLogOptions options,
      ISerializer<TKey> keySerializer,
      ISerializer<TValue> valueSerializer);

  IWriteAheadLog<TKey, TValue> GetWal<TKey, TValue>(long segmentId, string category);

  bool RemoveWal(long segmentId, string category);

  void DropStore();
}
