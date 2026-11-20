using System.Numerics;
using System.Runtime.InteropServices;
using ZoneTree.Hashers;

namespace ZoneTree.Threading;

/// <summary>
/// Locks keys. A key takes the lock of its stripe, which the key hasher chooses, so keys
/// that the comparer considers equal share a lock.
/// </summary>
/// <remarks>
/// <para>
/// Each stripe is a <see cref="HybridLock"/>, which sync and async callers share. Different
/// keys can share a stripe, and then they wait for each other: more stripes make that rarer.
/// Several keys take their distinct stripes in ascending order, so callers that lock the same
/// keys in a different order cannot deadlock.
/// </para>
/// <para>
/// The key hasher must match the comparer of the keys: keys that the comparer considers
/// equal must have the same hash. Otherwise equal keys can take different locks, and they do
/// not exclude each other. <see cref="KeyedLock{TKey}"/> does not check this. A ZoneTree
/// checks it for its own hasher when it opens.
/// </para>
/// <para>
/// The locks are not reentrant. A thread that holds a lock from
/// <see cref="Lock(in TKey)"/>, <see cref="LockMany(ReadOnlySpan{TKey})"/> or
/// <see cref="TryLock(in TKey, TimeSpan, out HybridLockScope)"/> and locks a key of that
/// stripe again, synchronously or asynchronously, gets a <see cref="LockRecursionException"/>.
/// A lock from an asynchronous call has no thread, so locking its key again waits forever.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
public sealed class KeyedLock<TKey>
{
  readonly IKeyHasher<TKey> KeyHasher;

  readonly int StripeCount;

  readonly int StripeBits;

  // Created on the first lock, so key locks that never lock a key allocate nothing.
  HybridLock[] Stripes;

  /// <summary>
  /// The largest stripe count, which keeps the stripe array within memory.
  /// </summary>
  public const int MaximumStripeCount = 1 << 20;

  /// <summary>
  /// Creates the key locks. A stripe allocates its lock when a key first takes it.
  /// </summary>
  /// <param name="keyHasher">Chooses the stripe of a key. It must give keys that the comparer
  /// considers equal the same hash. One stripe needs no hasher.</param>
  /// <param name="stripeCount">The number of locks, from 1 to
  /// <see cref="MaximumStripeCount"/>, rounded up to a power of two. One stripe locks every
  /// key with one lock.</param>
  /// <exception cref="ArgumentOutOfRangeException">The stripe count is out of range.</exception>
  /// <exception cref="ArgumentNullException">The key hasher is null, and there is more than
  /// one stripe.</exception>
  public KeyedLock(IKeyHasher<TKey> keyHasher, int stripeCount)
  {
    if (stripeCount < 1 || stripeCount > MaximumStripeCount)
      throw new ArgumentOutOfRangeException(
          nameof(stripeCount), stripeCount, $"The lock stripe count must be from 1 to {MaximumStripeCount}.");
    if (keyHasher == null && stripeCount > 1)
      throw new ArgumentNullException(
          nameof(keyHasher),
          "The key hasher chooses the lock of a key when there is more than one stripe. Pass a key " +
          "hasher that matches the comparer of the keys, or a stripe count of 1 to use one lock for every key.");
    KeyHasher = keyHasher;
    StripeCount = (int)BitOperations.RoundUpToPowerOf2((uint)stripeCount);
    StripeBits = BitOperations.Log2((uint)StripeCount);
  }

  /// <summary>
  /// Locks the key until the returned scope is disposed.
  /// </summary>
  /// <param name="key">The key to lock.</param>
  /// <returns>The scope that releases the lock when it is disposed.</returns>
  /// <exception cref="LockRecursionException">The thread already holds the lock of the key's
  /// stripe.</exception>
  public HybridLockScope Lock(in TKey key)
  {
    var stripe = GetStripe(GetStripeIndex(in key));
    // Only the contended path can fail, so explaining a failure costs the free lock nothing.
    if (!stripe.TryEnterUncontended())
      EnterContended(stripe);
    return new HybridLockScope(stripe);
  }

  static void EnterContended(HybridLock stripe)
  {
    try
    {
      stripe.EnterContended();
    }
    catch (LockRecursionException e)
    {
      throw SharedStripe(e);
    }
  }

  // A held stripe can be the lock of another key, which the lock's own message cannot tell.
  static LockRecursionException SharedStripe(LockRecursionException e) => new(
      "The thread already holds the lock of this key's stripe, so waiting for it would never end. " +
      "Keys can share a stripe, so the held lock can be the lock of another key. Release it first, " +
      "or lock all the keys with one LockMany call.", e);

  static LockRecursionException SharedStripeAsync(LockRecursionException e) => new(
      "The thread already holds the lock of this key's stripe from Lock, LockMany or TryLock, so " +
      "waiting for it would never end. Keys can share a stripe, so the held lock can be the lock of " +
      "another key. Release it first, or lock all the keys with one LockManyAsync call. Code that " +
      "awaits while it holds a key lock takes it with LockAsync or LockManyAsync.", e);

  static ValueTask<HybridLockScope> ExplainSharedStripe(ValueTask<HybridLockScope> pending)
  {
    var task = pending.AsTask();
    return task.Exception?.InnerException is LockRecursionException e
        ? ValueTask.FromException<HybridLockScope>(SharedStripeAsync(e))
        : new(task);
  }

  /// <summary>
  /// Locks the key until the returned scope is disposed, if its lock is free within the timeout.
  /// </summary>
  /// <param name="key">The key to lock.</param>
  /// <param name="timeout">How long to wait for the lock, rounded up to whole milliseconds.
  /// Zero does not wait, and <see cref="Timeout.InfiniteTimeSpan"/> waits until the lock is
  /// free.</param>
  /// <param name="scope">The scope that releases the lock when it is disposed, or the default
  /// value, which holds nothing, if the timeout passed.</param>
  /// <returns>true if the key is locked; otherwise, false.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The timeout is negative but not
  /// <see cref="Timeout.InfiniteTimeSpan"/>, or longer than <see cref="int.MaxValue"/>
  /// milliseconds.</exception>
  /// <exception cref="LockRecursionException">The thread already holds the lock of the key's
  /// stripe, and the call would wait.</exception>
  public bool TryLock(in TKey key, TimeSpan timeout, out HybridLockScope scope)
  {
    var stripe = GetStripe(GetStripeIndex(in key));
    bool taken;
    try
    {
      taken = stripe.TryEnter(timeout);
    }
    catch (LockRecursionException e)
    {
      throw SharedStripe(e);
    }
    if (!taken)
    {
      scope = default;
      return false;
    }
    scope = new HybridLockScope(stripe);
    return true;
  }

  /// <summary>
  /// Locks the key until the returned scope is disposed, without blocking the thread while
  /// it waits. The lock can be held across an await.
  /// </summary>
  /// <remarks>
  /// A held lock is often free again within microseconds, so the call spins for up to about
  /// 23 microseconds before it waits without the thread.
  /// </remarks>
  /// <param name="key">The key to lock.</param>
  /// <param name="cancellationToken">Cancels the wait for the lock.</param>
  /// <returns>The scope that releases the lock when it is disposed.</returns>
  /// <exception cref="OperationCanceledException">The wait was cancelled, and nothing is locked.</exception>
  /// <exception cref="LockRecursionException">The thread holds the lock of the key's stripe
  /// from a synchronous call, and nothing is locked.</exception>
  public ValueTask<HybridLockScope> LockAsync(TKey key, CancellationToken cancellationToken = default)
  {
    HybridLock stripe;
    try
    {
      stripe = GetStripe(GetStripeIndex(in key));
    }
    catch (Exception e)
    {
      // The error reaches the caller through the task, as it does when locking several keys.
      return ValueTask.FromException<HybridLockScope>(e);
    }
    // A cancelled call takes nothing, even a free lock.
    if (cancellationToken.IsCancellationRequested)
      return ValueTask.FromCanceled<HybridLockScope>(cancellationToken);
    // Only the contended path can fail, so explaining a failure costs the free lock nothing.
    if (stripe.TryTakeUncontended())
      return new(new HybridLockScope(stripe));
    return LockContendedAsync(stripe, cancellationToken);
  }

  static ValueTask<HybridLockScope> LockContendedAsync(HybridLock stripe, CancellationToken cancellationToken)
  {
    var pending = stripe.LockAsyncContended(cancellationToken);
    return pending.IsFaulted ? ExplainSharedStripe(pending) : pending;
  }

  /// <summary>
  /// Locks the keys until the returned scope is disposed.
  /// </summary>
  /// <remarks>
  /// It takes the lock of each stripe once, in ascending order, so callers that lock the same
  /// keys in a different order do not deadlock, and keys that share a stripe do not wait for
  /// themselves. Holding more than two distinct locks allocates an array for them.
  /// </remarks>
  /// <param name="keys">The keys to lock.</param>
  /// <returns>The scope that releases the locks when it is disposed.</returns>
  /// <exception cref="LockRecursionException">The thread already holds the lock of a stripe of
  /// the keys, and nothing is locked.</exception>
  public HybridLockScope LockMany(params ReadOnlySpan<TKey> keys)
  {
    Span<int> indexes = keys.Length <= 32 ? stackalloc int[keys.Length] : new int[keys.Length];
    for (var i = 0; i < keys.Length; ++i)
      indexes[i] = GetStripeIndex(in keys[i]);
    indexes = indexes[..SortDistinct(indexes)];
    for (var i = 0; i < indexes.Length; ++i)
    {
      try
      {
        GetStripe(indexes[i]).Enter();
      }
      catch (LockRecursionException e)
      {
        Release(indexes[..i]);
        throw SharedStripe(e);
      }
      catch
      {
        Release(indexes[..i]);
        throw;
      }
    }
    return CreateScope(indexes);
  }

  /// <summary>
  /// Locks the keys until the returned scope is disposed. C# 13 and later choose
  /// <see cref="LockMany(ReadOnlySpan{TKey})"/>, which needs no array for the keys; earlier
  /// versions call this overload.
  /// </summary>
  /// <param name="keys">The keys to lock.</param>
  /// <returns>The scope that releases the locks when it is disposed.</returns>
  /// <exception cref="ArgumentNullException">The keys are null.</exception>
  /// <exception cref="LockRecursionException">The thread already holds the lock of a stripe of
  /// the keys, and nothing is locked.</exception>
  public HybridLockScope LockMany(params TKey[] keys)
  {
    ArgumentNullException.ThrowIfNull(keys);
    return LockMany(new ReadOnlySpan<TKey>(keys));
  }

  /// <summary>
  /// Locks the keys until the returned scope is disposed, without blocking the thread while
  /// it waits. See <see cref="LockMany(ReadOnlySpan{TKey})"/> for how several keys are locked.
  /// </summary>
  /// <remarks>
  /// A collection expression such as <c>[a, b]</c> calls this overload, which needs no
  /// collection for the keys.
  /// </remarks>
  /// <param name="keys">The keys to lock.</param>
  /// <param name="cancellationToken">Cancels the wait for the locks.</param>
  /// <returns>The scope that releases the locks when it is disposed.</returns>
  /// <exception cref="OperationCanceledException">The wait was cancelled, and nothing is locked.</exception>
  /// <exception cref="LockRecursionException">The thread holds the lock of a stripe of the keys
  /// from a synchronous call, and nothing is locked.</exception>
  public ValueTask<HybridLockScope> LockManyAsync(
      ReadOnlySpan<TKey> keys, CancellationToken cancellationToken = default)
  {
    Span<int> indexes = keys.Length <= 32 ? stackalloc int[keys.Length] : new int[keys.Length];
    try
    {
      for (var i = 0; i < keys.Length; ++i)
        indexes[i] = GetStripeIndex(in keys[i]);
    }
    catch (Exception e)
    {
      // The error reaches the caller through the task, as it does when locking one key.
      return ValueTask.FromException<HybridLockScope>(e);
    }
    return LockStripesAsync(indexes[..SortDistinct(indexes)], cancellationToken);
  }

  /// <summary>
  /// Locks the keys until the returned scope is disposed, without blocking the thread while
  /// it waits. See <see cref="LockMany(ReadOnlySpan{TKey})"/> for how several keys are locked.
  /// </summary>
  /// <remarks>
  /// An array calls this overload, which reads the keys in place.
  /// </remarks>
  /// <param name="keys">The keys to lock.</param>
  /// <param name="cancellationToken">Cancels the wait for the locks.</param>
  /// <returns>The scope that releases the locks when it is disposed.</returns>
  /// <exception cref="ArgumentNullException">The keys are null.</exception>
  /// <exception cref="OperationCanceledException">The wait was cancelled, and nothing is locked.</exception>
  /// <exception cref="LockRecursionException">The thread holds the lock of a stripe of the keys
  /// from a synchronous call, and nothing is locked.</exception>
  public ValueTask<HybridLockScope> LockManyAsync(TKey[] keys, CancellationToken cancellationToken = default)
  {
    if (keys == null)
      return ValueTask.FromException<HybridLockScope>(new ArgumentNullException(nameof(keys)));
    return LockManyAsync(new ReadOnlySpan<TKey>(keys), cancellationToken);
  }

  /// <summary>
  /// Locks the keys until the returned scope is disposed, without blocking the thread while
  /// it waits. See <see cref="LockMany(ReadOnlySpan{TKey})"/> for how several keys are locked.
  /// </summary>
  /// <remarks>
  /// A list is read in place. Another sequence is copied to an array first.
  /// </remarks>
  /// <param name="keys">The keys to lock.</param>
  /// <param name="cancellationToken">Cancels the wait for the locks.</param>
  /// <returns>The scope that releases the locks when it is disposed.</returns>
  /// <exception cref="ArgumentNullException">The keys are null.</exception>
  /// <exception cref="OperationCanceledException">The wait was cancelled, and nothing is locked.</exception>
  /// <exception cref="LockRecursionException">The thread holds the lock of a stripe of the keys
  /// from a synchronous call, and nothing is locked.</exception>
  public ValueTask<HybridLockScope> LockManyAsync(
      IEnumerable<TKey> keys, CancellationToken cancellationToken = default)
  {
    if (keys is TKey[] array)
      return LockManyAsync(new ReadOnlySpan<TKey>(array), cancellationToken);
    if (keys is List<TKey> keyList)
      return LockManyAsync(CollectionsMarshal.AsSpan(keyList), cancellationToken);
    IReadOnlyList<TKey> list;
    int count;
    try
    {
      ArgumentNullException.ThrowIfNull(keys);
      // Another list, such as a collection expression passed as a sequence, is read in place.
      list = keys as IReadOnlyList<TKey> ?? keys.ToArray();
      count = list.Count;
    }
    catch (Exception e)
    {
      return ValueTask.FromException<HybridLockScope>(e);
    }
    Span<int> indexes = count <= 32 ? stackalloc int[count] : new int[count];
    try
    {
      for (var i = 0; i < count; ++i)
        indexes[i] = GetStripeIndex(list[i]);
    }
    catch (Exception e)
    {
      return ValueTask.FromException<HybridLockScope>(e);
    }
    return LockStripesAsync(indexes[..SortDistinct(indexes)], cancellationToken);
  }

  /// <summary>
  /// Locks the distinct stripes, in ascending order. Free stripes are taken without the cost
  /// of an async method, and the first stripe that waits continues asynchronously.
  /// </summary>
  ValueTask<HybridLockScope> LockStripesAsync(ReadOnlySpan<int> indexes, CancellationToken cancellationToken)
  {
    // A cancelled call takes nothing, even free stripes.
    if (cancellationToken.IsCancellationRequested)
      return ValueTask.FromCanceled<HybridLockScope>(cancellationToken);
    for (var i = 0; i < indexes.Length; ++i)
    {
      var stripe = GetStripe(indexes[i]);
      if (stripe.TryTake())
        continue;
      var pending = stripe.LockAsync(cancellationToken);
      if (!pending.IsCompletedSuccessfully)
        return LockRemainingAsync(indexes.ToArray(), i, pending, cancellationToken);
      // Returns a reused waiter, if the stripe was granted to one at once. The scope of one
      // stripe is not kept: the scope of all of them follows.
      _ = pending.Result;
    }
    return new(CreateScope(indexes));
  }

  async ValueTask<HybridLockScope> LockRemainingAsync(
      int[] indexes, int waiting, ValueTask<HybridLockScope> pending, CancellationToken cancellationToken)
  {
    var i = waiting;
    try
    {
      await pending.ConfigureAwait(false);
      for (++i; i < indexes.Length; ++i)
        await GetStripe(indexes[i]).LockAsync(cancellationToken).ConfigureAwait(false);
    }
    catch (LockRecursionException e)
    {
      Release(indexes.AsSpan(0, i));
      throw SharedStripeAsync(e);
    }
    catch
    {
      Release(indexes.AsSpan(0, i));
      throw;
    }
    return CreateScope(indexes);
  }

  /// <summary>
  /// Creates the scope of the locked stripes. One or two stripes need no array.
  /// </summary>
  HybridLockScope CreateScope(ReadOnlySpan<int> indexes) => indexes.Length switch
  {
    0 => default,
    1 => new HybridLockScope(GetStripe(indexes[0])),
    2 => new HybridLockScope(GetStripe(indexes[0]), GetStripe(indexes[1])),
    _ => new HybridLockScope(GetStripes(indexes))
  };

  void Release(ReadOnlySpan<int> indexes)
  {
    for (var i = indexes.Length - 1; i >= 0; --i)
      GetStripe(indexes[i]).Exit();
  }

  /// <summary>
  /// Sorts the stripe indexes, moves each one once to the start, and returns how many
  /// there are.
  /// </summary>
  static int SortDistinct(Span<int> indexes)
  {
    if (indexes.Length <= 16)
    {
      // An insertion sort is the fastest for a few keys.
      for (var i = 1; i < indexes.Length; ++i)
      {
        var index = indexes[i];
        var j = i;
        for (; j > 0 && indexes[j - 1] > index; --j)
          indexes[j] = indexes[j - 1];
        indexes[j] = index;
      }
    }
    else
    {
      indexes.Sort();
    }
    var count = 0;
    for (var i = 0; i < indexes.Length; ++i)
      if (count == 0 || indexes[i] != indexes[count - 1])
        indexes[count++] = indexes[i];
    return count;
  }

  HybridLock[] GetStripes(ReadOnlySpan<int> indexes)
  {
    var stripes = new HybridLock[indexes.Length];
    for (var i = 0; i < indexes.Length; ++i)
      stripes[i] = GetStripe(indexes[i]);
    return stripes;
  }

  int GetStripeIndex(in TKey key)
  {
    // One stripe needs no hasher, and the constructor refuses a missing one otherwise.
    if (StripeBits == 0)
      return 0;
    // Fibonacci hashing spreads hashes with weak low bits, such as the identity hash of an int.
    var hash = unchecked((uint)KeyHasher.GetHashCode(in key) * 2654435769u);
    return (int)(hash >> (32 - StripeBits));
  }

  HybridLock GetStripe(int index)
  {
    var stripes = Volatile.Read(ref Stripes);
    if (stripes == null)
    {
      Interlocked.CompareExchange(ref Stripes, new HybridLock[StripeCount], null);
      stripes = Stripes;
    }
    var stripe = Volatile.Read(ref stripes[index]);
    if (stripe != null)
      return stripe;
    var created = new HybridLock();
    return Interlocked.CompareExchange(ref stripes[index], created, null) ?? created;
  }
}
