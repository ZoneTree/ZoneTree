namespace ZoneTree.Cluster;

/// <summary>A snapshot of a standalone column, readable through the column view it implements.</summary>
/// <remarks>
/// Finish outstanding operations before ending the transaction. Iterators cannot be
/// used after the transaction ends.
/// </remarks>
public interface IStandaloneReadTransaction<TKey, TValue> :
    IReadColumn<TKey, TValue>, IDisposable, IAsyncDisposable
{
  /// <summary>The snapshot timestamp of the column.</summary>
  long SnapshotTimestamp { get; }
}
