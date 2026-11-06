namespace ZoneTree.Cluster;

/// <summary>
/// Counts the running creations of a column's views, transactions and iterators. Once
/// stopped, it rejects new ones and waits for the running ones, so a drop finds everything
/// registered with the column and none is registered after it.
/// </summary>
internal sealed class ColumnOperationGate
{
  const int Stopped = 1 << 30;

  int Operations;

  /// <summary>Starts an operation, or returns false once the gate stopped.</summary>
  internal bool TryBegin()
  {
    if ((Interlocked.Increment(ref Operations) & Stopped) == 0)
      return true;
    Interlocked.Decrement(ref Operations);
    return false;
  }

  internal void End() => Interlocked.Decrement(ref Operations);

  /// <summary>Rejects new operations and waits for the running ones to end.</summary>
  internal void Stop()
  {
    Interlocked.Or(ref Operations, Stopped);
    var wait = new SpinWait();
    while (Volatile.Read(ref Operations) != Stopped)
      wait.SpinOnce();
  }
}

/// <summary>
/// Something that uses a column and must end when the column is dropped: a view of a domain
/// transaction, a standalone transaction or a raw iterator.
/// </summary>
internal interface IColumnDependent
{
  /// <summary>
  /// Ends the use of the dropped column. Called after the column's operations stopped and
  /// under its commit gate.
  /// </summary>
  void OnColumnDropped();
}
