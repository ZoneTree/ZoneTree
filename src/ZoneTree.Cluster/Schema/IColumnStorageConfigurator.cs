namespace ZoneTree.Cluster;

/// <summary>Configures storage defaults for independently typed columns.</summary>
public interface IColumnStorageConfigurator
{
  /// <summary>
  /// Called when creating or opening each column, before its own ConfigureStorage hook.
  /// </summary>
  void Configure<TKey, TValue>(ColumnDefinition column, ZoneTreeFactory<TKey, TValue> storage);
}
