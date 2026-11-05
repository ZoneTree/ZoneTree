namespace ZoneTree.Cluster;

/// <summary>A snapshot of a standalone column, readable through the column view it implements.</summary>
/// <remarks>
/// Its operations may run concurrently. Ending the transaction waits for the running
/// operations, and one that starts while the transaction ends throws. Iterators cannot be
/// used after the transaction ends.
/// </remarks>
public interface IStandaloneReadTransaction<TKey, TValue> :
    IReadColumn<TKey, TValue>, IDisposable, IAsyncDisposable
{
  /// <summary>The snapshot timestamp of the column.</summary>
  long SnapshotTimestamp { get; }
}
