namespace ZoneTree.Transactional.Distributed;

/// <summary>An untyped schema entry used only during database configuration.</summary>
public abstract class DistributedColumnDefinition
{
  /// <summary>The persisted column name, compared using ordinal comparison.</summary>
  public string Name { get; }

  // Only the typed definition can introduce a column schema entry.
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
  /// <summary>Declares one column. Duplicate names are rejected when opening the database.</summary>
  public DistributedColumnDefinition(string name) : base(name) { }

  /// <summary>
  /// Optional startup-only configuration of the existing typed storage factory,
  /// including comparers, serializers, caches, compression and segment sizing.
  /// Known types use the normal ZoneTree defaults. Do not open a tree in this callback.
  /// Database-managed paths, transaction machinery and WAL ordering cannot be
  /// overridden. The in-process factory selects Shared WALs and rejects settings or
  /// custom providers that cannot supply the database's durability guarantees.
  /// </summary>
  public Action<ZoneTreeFactory<TKey, TValue>> ConfigureStorage { get; init; }
}
