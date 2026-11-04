using ZoneTree.Options;

namespace ZoneTree.Transactional.Distributed;

/// <summary>Declares a non-transactional column's types and storage options.</summary>
public sealed class DistributedRawColumnDefinition<TKey, TValue> : DistributedColumnDefinition
{
  internal override Type KeyType => typeof(TKey);
  internal override Type ValueType => typeof(TValue);
  internal override bool IsRaw => true;

  public DistributedRawColumnDefinition(string name) : base(name) { }

  /// <summary>Configures the column's plain ZoneTree storage.</summary>
  public Action<ZoneTreeFactory<TKey, TValue>> ConfigureStorage { get; init; }

  internal override DistributedDatabaseColumn Open(DistributedDatabase database, int index, bool create)
  {
    var factory = new ZoneTreeFactory<TKey, TValue>()
        .SetLogger(database.Logger)
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.Shared);
    ConfigureStorage?.Invoke(factory);
    factory.SetDataDirectory(Path.Combine(database.DataDirectory, $"column-{index:D6}"));
    var tree = create ? factory.Create() : factory.Open();
    return new DistributedRawColumn<TKey, TValue>(database, index, Name, tree);
  }
}
