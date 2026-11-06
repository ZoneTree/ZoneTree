namespace ZoneTree.Cluster;

/// <summary>
/// A transaction of a standalone column, which reads and writes through the column view it implements.
/// </summary>
/// <remarks>Disposing an active transaction rolls it back.</remarks>
public interface IStandaloneTransaction<TKey, TValue> :
    IStandaloneReadTransaction<TKey, TValue>, IWriteColumn<TKey, TValue>, IClusterWriteTransaction
{
}
