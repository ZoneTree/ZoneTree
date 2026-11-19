namespace ZoneTree.Core;

public sealed partial class ZoneTree<TKey, TValue> : IZoneTree<TKey, TValue>, IZoneTreeMaintenance<TKey, TValue>
{
  public KeyLockScope Lock(in TKey key) => KeyLocks.Lock(in key);

  public KeyLockScope LockMany(params ReadOnlySpan<TKey> keys) => KeyLocks.LockMany(keys);

  public KeyLockScope LockMany(params TKey[] keys)
  {
    ArgumentNullException.ThrowIfNull(keys);
    return KeyLocks.LockMany(keys);
  }

  public bool TryLock(in TKey key, TimeSpan timeout, out KeyLockScope scope)
      => KeyLocks.TryLock(in key, timeout, out scope);

  public ValueTask<KeyLockScope> LockAsync(TKey key, CancellationToken cancellationToken = default)
      => KeyLocks.LockAsync(key, cancellationToken);

  public ValueTask<KeyLockScope> LockManyAsync(IEnumerable<TKey> keys, CancellationToken cancellationToken = default)
      => KeyLocks.LockManyAsync(keys, cancellationToken);
}
