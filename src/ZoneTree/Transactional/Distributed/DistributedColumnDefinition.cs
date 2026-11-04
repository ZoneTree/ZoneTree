namespace ZoneTree.Transactional.Distributed;

/// <summary>A column schema entry.</summary>
public abstract class DistributedColumnDefinition
{
  /// <summary>The persisted column name, compared using ordinal comparison.</summary>
  public string Name { get; }

  internal abstract Type KeyType { get; }
  internal abstract Type ValueType { get; }
  internal abstract DistributedDatabaseColumn Open(
      DistributedDatabase database, int index, bool create);

  private protected DistributedColumnDefinition(string name)
  {
    if (string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("A column needs a nonempty name.", nameof(name));
    Name = name;
  }
}

/// <summary>Declares a column's key and value types and optional storage tuning.</summary>
public sealed class DistributedColumnDefinition<TKey, TValue> : DistributedColumnDefinition
{
  internal override Type KeyType => typeof(TKey);
  internal override Type ValueType => typeof(TValue);

  /// <summary>Declares one column. Duplicate names are rejected when opening the database.</summary>
  public DistributedColumnDefinition(string name) : base(name) { }

  /// <summary>
  /// Configures the column's storage options, such as comparers, serializers and compression.
  /// </summary>
  public Action<ZoneTreeFactory<TKey, TValue>> ConfigureStorage { get; init; }

  internal override DistributedDatabaseColumn Open(
      DistributedDatabase database, int index, bool create)
  {
    var factory = new ZoneTreeFactory<TKey, TValue>()
        .SetLogger(database.Logger)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = Options.WriteAheadLogMode.Shared);
    ConfigureStorage?.Invoke(factory);
    factory.SetDataDirectory(Path.Combine(database.DataDirectory, $"column-{index:D6}"));
    return new DistributedDatabaseColumn<TKey, TValue>(database, index, Name,
        factory.OpenDistributedColumn(canCreate: create, canOpen: !create));
  }
}
