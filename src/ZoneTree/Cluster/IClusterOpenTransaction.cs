namespace ZoneTree.Cluster;

/// <summary>A transaction the cluster rolls back when it closes, if the transaction is still active.</summary>
internal interface IClusterOpenTransaction
{
  /// <summary>
  /// Rolls the transaction back when the cluster closes. A transaction whose commit
  /// started is left to finish: closing a column publishes its pending commits.
  /// </summary>
  void RollbackOnClose();
}
