namespace ZoneTree.Threading;

/// <summary>
/// Coordinates leases with deferred, exactly-once resource retirement.
/// </summary>
struct LifecycleLeaseState
{
  const int StateShift = 61;

  const long LeaseCountMask = (1L << StateShift) - 1;

  const long LifecycleMask = ~LeaseCountMask;

  const long Active = 0;

  // The retirement-request bit is monotonic. Every subsequent lifecycle state
  // retains it, allowing RequestRetirement to use one atomic OR.
  const long RetirementRequested = 1L << StateShift;

  const long Retiring = RetirementRequested | (1L << (StateShift + 1));

  const long Retired = Retiring | long.MinValue;

  long State;

  public long LeaseCount => Volatile.Read(ref State) & LeaseCountMask;

  /// <summary>
  /// Tries to acquire a lease while the resource is active.
  /// </summary>
  public bool TryAcquire()
  {
    while (true)
    {
      var state = Volatile.Read(ref State);
      if ((state & LifecycleMask) != Active)
        return false;
      if ((state & LeaseCountMask) == LeaseCountMask)
        throw new InvalidOperationException("The lifecycle lease count reached its maximum value.");
      if (Interlocked.CompareExchange(ref State, state + 1, state) == state)
        return true;
    }
  }

  /// <summary>
  /// Releases a lease and returns true when the final lease makes a previously
  /// requested retirement eligible for completion.
  /// </summary>
  public bool Release()
  {
    while (true)
    {
      var state = Volatile.Read(ref State);
      var leaseCount = state & LeaseCountMask;
      if (leaseCount == 0)
        throw new InvalidOperationException("The lifecycle does not have an active lease to release.");

      var lifecycle = state & LifecycleMask;
      if (lifecycle is not Active and not RetirementRequested)
        throw new InvalidOperationException("The lifecycle cannot release a lease in its current state.");

      var completeRetirement = lifecycle == RetirementRequested && leaseCount == 1;
      var nextState = state - 1;
      if (Interlocked.CompareExchange(ref State, nextState, state) == state)
        return completeRetirement;
    }
  }

  /// <summary>
  /// Requests retirement without waiting for active leases. Returns true only
  /// when this caller owns retirement completion because no leases remain.
  /// </summary>
  public bool RequestRetirement()
  {
    var previousState = Interlocked.Or(ref State, RetirementRequested);
    var lifecycle = previousState & LifecycleMask;
    if (lifecycle is Retiring or Retired ||
        (previousState & LeaseCountMask) != 0)
      return false;

    return Interlocked.CompareExchange(
        ref State,
        Retiring,
        RetirementRequested) == RetirementRequested;
  }

  /// <summary>
  /// Tries to own retirement completion after the final lease has been
  /// released.
  /// </summary>
  public bool TryBeginRetirementCompletion()
  {
    return Interlocked.CompareExchange(
        ref State,
        Retiring,
        RetirementRequested) == RetirementRequested;
  }

  public void CompleteRetirement()
  {
    if (Interlocked.CompareExchange(ref State, Retired, Retiring) != Retiring)
      throw new InvalidOperationException("The lifecycle is not completing retirement.");
  }

  public void CancelRetirementCompletion()
  {
    if (Interlocked.CompareExchange(
        ref State,
        RetirementRequested,
        Retiring) != Retiring)
      throw new InvalidOperationException("The lifecycle is not completing retirement.");
  }
}
