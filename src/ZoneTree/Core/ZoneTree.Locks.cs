using ZoneTree.Threading;

namespace ZoneTree.Core;

public sealed partial class ZoneTree<TKey, TValue> : IZoneTree<TKey, TValue>, IZoneTreeMaintenance<TKey, TValue>
{
  public HybridLockScope Lock(in TKey key) => KeyLocks.Lock(in key);

  public HybridLockScope LockMany(params ReadOnlySpan<TKey> keys) => KeyLocks.LockMany(keys);

  public HybridLockScope LockMany(params TKey[] keys) => KeyLocks.LockMany(keys);

  public bool TryLock(in TKey key, TimeSpan timeout, out HybridLockScope scope)
      => KeyLocks.TryLock(in key, timeout, out scope);

  public ValueTask<HybridLockScope> LockAsync(TKey key, CancellationToken cancellationToken = default)
      => KeyLocks.LockAsync(key, cancellationToken);

  public ValueTask<HybridLockScope> LockManyAsync(ReadOnlySpan<TKey> keys, CancellationToken cancellationToken = default)
      => KeyLocks.LockManyAsync(keys, cancellationToken);

  public ValueTask<HybridLockScope> LockManyAsync(TKey[] keys, CancellationToken cancellationToken = default)
      => KeyLocks.LockManyAsync(keys, cancellationToken);

  public ValueTask<HybridLockScope> LockManyAsync(IEnumerable<TKey> keys, CancellationToken cancellationToken = default)
      => KeyLocks.LockManyAsync(keys, cancellationToken);
}
