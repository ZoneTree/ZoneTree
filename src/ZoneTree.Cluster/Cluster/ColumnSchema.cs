using ZoneTree.Comparers;
using ZoneTree.Serializers;

namespace ZoneTree.Cluster;

/// <summary>
/// Applies the schema of a column definition: the comparer and serializers that the hosts
/// and the clients of a cluster share. Storage hooks run afterward.
/// </summary>
internal static class ColumnSchema
{
  internal static ZoneTreeFactory<TKey, TValue> Apply<TKey, TValue>(
      ZoneTreeFactory<TKey, TValue> factory,
      IRefComparer<TKey> comparer,
      ISerializer<TKey> keySerializer,
      ISerializer<TValue> valueSerializer)
  {
    if (comparer != null)
      factory.SetComparer(comparer);
    if (keySerializer != null)
      factory.SetKeySerializer(keySerializer);
    if (valueSerializer != null)
      factory.SetValueSerializer(valueSerializer);
    return factory;
  }

  /// <summary>
  /// Checks that the storage hooks kept the schema. Hosts and clients encode and order
  /// keys with the schema, so a hook that replaced it would make them disagree.
  /// </summary>
  internal static void Verify<TKey, TValue>(
      ZoneTreeFactory<TKey, TValue> factory,
      string columnName,
      IRefComparer<TKey> comparer,
      ISerializer<TKey> keySerializer,
      ISerializer<TValue> valueSerializer)
  {
    var options = factory.Options;
    if ((comparer != null && !ReferenceEquals(options.Comparer, comparer)) ||
        (keySerializer != null && !ReferenceEquals(options.KeySerializer, keySerializer)) ||
        (valueSerializer != null && !ReferenceEquals(options.ValueSerializer, valueSerializer)))
      throw new ArgumentException(
          $"The storage configuration of column '{columnName}' replaced its comparer or serializers. Set them only in the column definition.");
  }
}
