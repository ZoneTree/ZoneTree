using System.Numerics;
using ZoneTree.Exceptions;
using ZoneTree.Hashers;
using ZoneTree.Options;

namespace ZoneTree.Core;

/// <summary>
/// The key locks of a tree. A key takes the lock of its stripe, which the key hasher
/// chooses, so keys that the comparer considers equal share a lock.
/// </summary>
/// <remarks>
/// The locks are semaphores, so a caller can hold one across an await, and sync and
/// async callers exclude each other. Several keys take their distinct stripes in
/// ascending order, so callers that lock the same keys in a different order cannot deadlock.
/// </remarks>
internal sealed class KeyLocks<TKey>
{
  readonly IKeyHasher<TKey> KeyHasher;

  readonly int StripeCount;

  readonly int StripeBits;

  // Created on the first lock, so a tree that never locks a key allocates nothing.
  SemaphoreSlim[] Stripes;

  /// <summary>
  /// The largest stripe count, which keeps the stripe array within memory.
  /// </summary>
  public const int MaximumStripeCount = 1 << 20;

  public KeyLocks(IKeyHasher<TKey> keyHasher, int stripeCount)
  {
    // A tree created without the validator still needs a usable count.
    if (stripeCount < 1 || stripeCount > MaximumStripeCount)
      throw new ArgumentOutOfRangeException(
          nameof(stripeCount), stripeCount, $"The lock stripe count must be from 1 to {MaximumStripeCount}.");
    KeyHasher = keyHasher;
    StripeCount = (int)BitOperations.RoundUpToPowerOf2((uint)stripeCount);
    StripeBits = BitOperations.Log2((uint)StripeCount);
  }

  public KeyLockScope Lock(in TKey key)
  {
    var stripe = GetStripe(GetStripeIndex(in key));
    stripe.Wait();
    return new KeyLockScope(stripe);
  }

  public bool TryLock(in TKey key, TimeSpan timeout, out KeyLockScope scope)
  {
    var stripe = GetStripe(GetStripeIndex(in key));
    if (!stripe.Wait(timeout))
    {
      scope = default;
      return false;
    }
    scope = new KeyLockScope(stripe);
    return true;
  }

  public ValueTask<KeyLockScope> LockAsync(TKey key, CancellationToken cancellationToken)
  {
    SemaphoreSlim stripe;
    try
    {
      stripe = GetStripe(GetStripeIndex(in key));
    }
    catch (Exception e)
    {
      // The error reaches the caller through the task, as it does when locking several keys.
      return ValueTask.FromException<KeyLockScope>(e);
    }
    var wait = stripe.WaitAsync(cancellationToken);
    // An uncontended wait completes at once, without an allocation.
    if (wait.IsCompletedSuccessfully)
      return new(new KeyLockScope(stripe));
    return WaitAsync(wait, stripe);

    static async ValueTask<KeyLockScope> WaitAsync(Task wait, SemaphoreSlim stripe)
    {
      await wait.ConfigureAwait(false);
      return new KeyLockScope(stripe);
    }
  }

  public KeyLockScope LockMany(ReadOnlySpan<TKey> keys)
  {
    Span<int> indexes = keys.Length <= 32 ? stackalloc int[keys.Length] : new int[keys.Length];
    indexes = indexes[..GetDistinctStripeIndexes(keys, indexes)];
    for (var i = 0; i < indexes.Length; ++i)
    {
      try
      {
        GetStripe(indexes[i]).Wait();
      }
      catch
      {
        Release(indexes[..i]);
        throw;
      }
    }
    return CreateScope(indexes);
  }

  public async ValueTask<KeyLockScope> LockManyAsync(IEnumerable<TKey> keys, CancellationToken cancellationToken)
  {
    var keyArray = keys as TKey[] ?? keys.ToArray();
    var indexes = new int[keyArray.Length];
    var count = GetDistinctStripeIndexes(keyArray, indexes);
    for (var i = 0; i < count; ++i)
    {
      try
      {
        await GetStripe(indexes[i]).WaitAsync(cancellationToken).ConfigureAwait(false);
      }
      catch
      {
        Release(indexes.AsSpan(0, i));
        throw;
      }
    }
    return CreateScope(indexes.AsSpan(0, count));
  }

  /// <summary>
  /// Creates the scope of the locked stripes. One or two stripes need no array.
  /// </summary>
  KeyLockScope CreateScope(ReadOnlySpan<int> indexes) => indexes.Length switch
  {
    0 => default,
    1 => new KeyLockScope(GetStripe(indexes[0])),
    2 => new KeyLockScope(GetStripe(indexes[0]), GetStripe(indexes[1])),
    _ => new KeyLockScope(GetStripes(indexes))
  };

  void Release(ReadOnlySpan<int> indexes)
  {
    for (var i = indexes.Length - 1; i >= 0; --i)
      GetStripe(indexes[i]).Release();
  }

  /// <summary>
  /// Writes the stripe indexes of the keys, each once, in ascending order, to the start
  /// of the indexes, and returns how many there are.
  /// </summary>
  int GetDistinctStripeIndexes(ReadOnlySpan<TKey> keys, Span<int> indexes)
  {
    for (var i = 0; i < keys.Length; ++i)
      indexes[i] = GetStripeIndex(in keys[i]);
    indexes.Sort();
    var count = 0;
    for (var i = 0; i < indexes.Length; ++i)
      if (count == 0 || indexes[i] != indexes[count - 1])
        indexes[count++] = indexes[i];
    return count;
  }

  SemaphoreSlim[] GetStripes(ReadOnlySpan<int> indexes)
  {
    var stripes = new SemaphoreSlim[indexes.Length];
    for (var i = 0; i < indexes.Length; ++i)
      stripes[i] = GetStripe(indexes[i]);
    return stripes;
  }

  int GetStripeIndex(in TKey key)
  {
    if (StripeBits == 0)
      return 0;
    if (KeyHasher == null)
      throw new MissingOptionException(
          nameof(ZoneTreeOptions<,>.KeyHasher),
          "ZoneTree chooses the lock of a key with the key hasher. Configure a KeyHasher that " +
          "matches the comparer, or set ZoneTreeOptions.LockStripeCount to 1 to use one lock for every key.");
    // Fibonacci hashing spreads hashes with weak low bits, such as the identity hash of an int.
    var hash = unchecked((uint)KeyHasher.GetHashCode(in key) * 2654435769u);
    return (int)(hash >> (32 - StripeBits));
  }

  SemaphoreSlim GetStripe(int index)
  {
    var stripes = Volatile.Read(ref Stripes);
    if (stripes == null)
    {
      Interlocked.CompareExchange(ref Stripes, new SemaphoreSlim[StripeCount], null);
      stripes = Stripes;
    }
    var stripe = Volatile.Read(ref stripes[index]);
    if (stripe != null)
      return stripe;
    var created = new SemaphoreSlim(1, 1);
    return Interlocked.CompareExchange(ref stripes[index], created, null) ?? created;
  }
}
