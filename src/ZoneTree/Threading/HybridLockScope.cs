
namespace ZoneTree.Threading;

/// <summary>
/// Holds the locks that a call such as <see cref="HybridLock.Lock"/>
/// or <see cref="IZoneTree{TKey, TValue}.Lock(in TKey)"/> took, until it is disposed.
/// </summary>
/// <remarks>
/// Dispose it exactly once, for example with a using statement. A copy holds the same locks,
/// so disposing a copy as well would release locks that another caller took in between, or
/// throw a <see cref="SynchronizationLockException"/> when nobody holds them.
/// Disposing the default value does nothing.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1815", Justification = "Scopes are not compared.")]
public readonly struct HybridLockScope : IDisposable
{
  readonly HybridLock First;

  readonly HybridLock Second;

  // Set instead of First and Second when the scope holds more than two locks.
  readonly HybridLock[] All;

  internal HybridLockScope(HybridLock first, HybridLock second = null)
  {
    First = first;
    Second = second;
    All = null;
  }

  internal HybridLockScope(HybridLock[] all)
  {
    First = null;
    Second = null;
    All = all;
  }

  /// <summary>
  /// Releases the locks, in the reverse order of taking them.
  /// </summary>
  public void Dispose()
  {
    if (All != null)
    {
      for (var i = All.Length - 1; i >= 0; --i)
        All[i].Exit();
      return;
    }
    Second?.Exit();
    First?.Exit();
  }
}
