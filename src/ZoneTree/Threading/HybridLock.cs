using System.Diagnostics;
using System.Threading.Tasks.Sources;

namespace ZoneTree.Threading;

/// <summary>
/// A lock that sync and async callers share. It costs about as much as a lock statement, a
/// lock taken asynchronously can be held across an await, and waiting asynchronously
/// allocates nothing.
/// </summary>
/// <remarks>
/// <para>
/// Take the lock with <see cref="Lock"/>, <see cref="TryLock(TimeSpan, out HybridLockScope)"/>
/// or <see cref="LockAsync(CancellationToken)"/>, and dispose the returned scope, for example
/// with a using statement. <see cref="Enter"/>, <see cref="TryEnter(TimeSpan)"/> and
/// <see cref="Exit"/> do the same without a scope, for code that releases the lock itself.
/// </para>
/// <para>
/// A free lock is taken with one atomic instruction and released with another. A caller that
/// finds the lock held spins for up to about 23 microseconds, since a short locked section
/// often ends by then, and then waits: a synchronous caller blocks its thread, and an
/// asynchronous caller gives its thread back.
/// </para>
/// <para>
/// Sync and async waiters queue together, in arrival order. A running caller can take a freed
/// lock before a woken waiter does, but a waiter that loses it for about a millisecond gets it
/// handed over, so no caller starves.
/// </para>
/// <para>
/// The lock is not reentrant. A thread that holds it from <see cref="Enter"/>,
/// <see cref="TryEnter(TimeSpan)"/>, <see cref="Lock"/> or
/// <see cref="TryLock(TimeSpan, out HybridLockScope)"/> and takes it again, synchronously or
/// asynchronously, gets a <see cref="LockRecursionException"/>. A lock from
/// <see cref="LockAsync(CancellationToken)"/> has no thread, so taking it again in the same
/// asynchronous flow waits forever.
/// </para>
/// <para>
/// A lock taken synchronously belongs to its thread. Code that awaits while it holds the lock
/// takes it with <see cref="LockAsync(CancellationToken)"/>: during the await, its thread runs
/// other code, which gets a <see cref="LockRecursionException"/> if it takes the lock.
/// </para>
/// <para>
/// Any thread can release the lock, so a lock from <see cref="LockAsync(CancellationToken)"/>
/// can be released on another thread after an await. Releasing a free lock throws a
/// <see cref="SynchronizationLockException"/>. Releasing a lock that another caller took is
/// not detected, so release each lock exactly once.
/// </para>
/// <para>
/// Do not lock on the instance itself, for example with a lock statement. The lock guards its
/// queue of waiters by locking the instance, which costs no memory, so a lock on it holds up
/// every caller that waits for the lock or releases it while others wait.
/// </para>
/// <para>
/// All members are thread-safe.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2002",
    Justification = "Locking the instance guards the queue of waiters without a lock object of its own, which would cost memory per lock. The docs tell callers not to lock on the instance.")]
public sealed class HybridLock
{
  // How it works. A caller that finds the lock held spins, checking it less and less often,
  // and then queues. A release frees the lock and wakes the first waiter, unless a spinning
  // caller is about to take it. The woken waiter competes with running callers for the lock:
  // a running caller takes a freed lock at once, while a woken waiter needs microseconds to
  // run. A woken waiter that loses and has waited longer than a millisecond goes back to the
  // front of the queue and makes the releases hand the lock to the first waiter instead, so no
  // waiter waits much longer than that behind callers that came later. The releases free the
  // lock again once a waiter gets it within the millisecond, or nobody waits.
  //
  // A waiter that gives up, after a timeout or a cancellation, races the release that wakes
  // it or hands it the lock. One atomic change of its status decides which comes first.

  const int Held = 1;

  // A woken waiter or a spinning caller competes for the freed lock, so releases wake no
  // other waiter. Each caller that sets the flag clears it when it takes the lock, queues or
  // gives up.
  const int Woken = 2;

  // Releases hand the lock to the first waiter instead of freeing it. Only set while the
  // lock is held, and never together with the woken flag.
  const int Starving = 4;

  // The state counts the queued waiters above the flags.
  const int OneWaiter = 8;

  const int Waiting = 0;

  const int Granted = 1;

  const int Signaled = 2;

  const int GivenUp = 3;

  // How long a waiter may lose the freed lock to running callers before releases hand it over.
  static readonly long StarvationTicks = Stopwatch.Frequency / 1000;

  // A spinning caller checks the lock after 8 normalized spin iterations, about 0.3
  // microseconds, then after twice as many each time up to 128, for 8 checks: about 23
  // microseconds in all. Checking rarely leaves the lock's cache line to the holder, which
  // releases and takes the lock faster then. Measured, this beats both checking more often
  // and the spinning of a plain lock statement, from 2 to 8 threads.
  const int FirstSpinIterations = 8;

  const int MaximumSpinIterations = 128;

  const int SpinCount = 8;

  static readonly bool IsSingleProcessor = Environment.ProcessorCount == 1;

  // Async waiters are reused, so a contended async wait allocates nothing.
  const int MaximumFreeAsyncWaiters = 4;

  // The spins of a queued sync waiter before it sleeps: busy spins, then yields.
  const int WaiterSpinCount = 20;

  int State;

  // The thread that holds the lock synchronously, or 0. It only detects recursion.
  int OwnerThreadId;

  // The queue and the free async waiters, guarded by locking this object.
  // Only contended calls take that lock.
  Waiter Head;

  Waiter Tail;

  AsyncWaiter FreeAsyncWaiters;

  int FreeAsyncWaiterCount;

  // A thread waits for one lock at a time, so it reuses one waiter.
  [ThreadStatic]
  static SyncWaiter ThreadWaiter;

  /// <summary>
  /// Takes the lock, waiting until it is free. Release it with <see cref="Exit"/>.
  /// </summary>
  /// <exception cref="LockRecursionException">The thread already holds the lock.</exception>
  public void Enter()
  {
    if (Interlocked.CompareExchange(ref State, Held, 0) == 0)
    {
      OwnerThreadId = Environment.CurrentManagedThreadId;
      return;
    }
    EnterSlow(Timeout.Infinite);
  }

  /// <summary>
  /// Takes the lock if it is free within the timeout. Release it with <see cref="Exit"/>.
  /// </summary>
  /// <param name="timeout">How long to wait for the lock, rounded up to whole milliseconds.
  /// Zero does not wait, and <see cref="Timeout.InfiniteTimeSpan"/> waits until the lock is
  /// free.</param>
  /// <returns>true if the lock was taken; otherwise, false.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The timeout is negative but not
  /// <see cref="Timeout.InfiniteTimeSpan"/>, or longer than <see cref="int.MaxValue"/>
  /// milliseconds.</exception>
  /// <exception cref="LockRecursionException">The thread already holds the lock, and the call
  /// would wait.</exception>
  public bool TryEnter(TimeSpan timeout)
  {
    var milliseconds = ToMilliseconds(timeout);
    if (milliseconds == 0 ? TryTake() : Interlocked.CompareExchange(ref State, Held, 0) == 0)
    {
      OwnerThreadId = Environment.CurrentManagedThreadId;
      return true;
    }
    return milliseconds != 0 && EnterSlow(milliseconds);
  }

  /// <summary>
  /// Takes the lock until the returned scope is disposed, waiting until it is free.
  /// </summary>
  /// <returns>The scope that releases the lock when it is disposed.</returns>
  /// <exception cref="LockRecursionException">The thread already holds the lock.</exception>
  public HybridLockScope Lock()
  {
    Enter();
    return new HybridLockScope(this);
  }

  /// <summary>
  /// Takes the lock until the returned scope is disposed, if it is free within the timeout.
  /// </summary>
  /// <param name="timeout">How long to wait for the lock, rounded up to whole milliseconds.
  /// Zero does not wait, and <see cref="Timeout.InfiniteTimeSpan"/> waits until the lock is
  /// free.</param>
  /// <param name="scope">The scope that releases the lock when it is disposed, or the default
  /// value, which holds nothing, if the timeout passed.</param>
  /// <returns>true if the lock was taken; otherwise, false.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The timeout is negative but not
  /// <see cref="Timeout.InfiniteTimeSpan"/>, or longer than <see cref="int.MaxValue"/>
  /// milliseconds.</exception>
  /// <exception cref="LockRecursionException">The thread already holds the lock, and the call
  /// would wait.</exception>
  public bool TryLock(TimeSpan timeout, out HybridLockScope scope)
  {
    if (!TryEnter(timeout))
    {
      scope = default;
      return false;
    }
    scope = new HybridLockScope(this);
    return true;
  }

  /// <summary>
  /// Takes the lock until the returned scope is disposed, without blocking the thread while
  /// it waits. The lock can be held across an await.
  /// </summary>
  /// <remarks>
  /// A held lock is often free again within microseconds, so the call spins for up to about
  /// 23 microseconds before it waits without the thread.
  /// </remarks>
  /// <param name="cancellationToken">Cancels the wait for the lock. A call with a cancelled
  /// token takes nothing, even a free lock.</param>
  /// <returns>The scope that releases the lock when it is disposed.</returns>
  /// <exception cref="OperationCanceledException">The wait was cancelled, and the lock is not
  /// taken.</exception>
  /// <exception cref="LockRecursionException">The thread holds the lock from a synchronous
  /// call, and the lock is not taken again.</exception>
  public ValueTask<HybridLockScope> LockAsync(CancellationToken cancellationToken = default)
  {
    // A cancelled call takes nothing, even a free lock.
    if (cancellationToken.IsCancellationRequested)
      return ValueTask.FromCanceled<HybridLockScope>(cancellationToken);
    if (Interlocked.CompareExchange(ref State, Held, 0) == 0)
      return new(new HybridLockScope(this));
    return LockAsyncSlow(cancellationToken);
  }

  /// <summary>
  /// Takes the lock if nobody holds it or waits for it, as <see cref="Enter"/> does first.
  /// After a miss, the caller calls <see cref="EnterContended"/>.
  /// </summary>
  internal bool TryEnterUncontended()
  {
    if (Interlocked.CompareExchange(ref State, Held, 0) != 0)
      return false;
    OwnerThreadId = Environment.CurrentManagedThreadId;
    return true;
  }

  /// <summary>
  /// Takes the lock after <see cref="TryEnterUncontended"/> missed, as <see cref="Enter"/>
  /// does then. It does not try the missed instruction again, which costs a contended lock.
  /// </summary>
  internal void EnterContended() => EnterSlow(Timeout.Infinite);

  /// <summary>
  /// Takes the lock for an asynchronous caller if nobody holds it or waits for it, as
  /// <see cref="LockAsync(CancellationToken)"/> does after checking the token. After a miss,
  /// the caller calls <see cref="LockAsyncContended(CancellationToken)"/>.
  /// </summary>
  internal bool TryTakeUncontended() => Interlocked.CompareExchange(ref State, Held, 0) == 0;

  /// <summary>
  /// Takes the lock asynchronously after <see cref="TryTakeUncontended"/> missed, as
  /// <see cref="LockAsync(CancellationToken)"/> does then.
  /// </summary>
  internal ValueTask<HybridLockScope> LockAsyncContended(CancellationToken cancellationToken)
      => LockAsyncSlow(cancellationToken);

  /// <summary>
  /// Releases the lock.
  /// </summary>
  /// <exception cref="SynchronizationLockException">The lock is free.</exception>
  public void Exit()
  {
    OwnerThreadId = 0;
    var state = Interlocked.CompareExchange(ref State, 0, Held);
    if (state != Held)
      ExitSlow(state);
  }

  /// <summary>
  /// Converts the timeout to milliseconds, rounding a fraction up, so a caller that asks to
  /// wait does wait. Only <see cref="Timeout.InfiniteTimeSpan"/> is infinite.
  /// </summary>
  internal static long ToMilliseconds(TimeSpan timeout)
  {
    if (timeout == Timeout.InfiniteTimeSpan)
      return Timeout.Infinite;
    var ticks = timeout.Ticks;
    var milliseconds = ticks < 0
        ? -1
        : ticks / TimeSpan.TicksPerMillisecond + (ticks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);
    if (milliseconds < 0 || milliseconds > int.MaxValue)
      throw new ArgumentOutOfRangeException(
          nameof(timeout), timeout, "The timeout must be from zero to int.MaxValue milliseconds, or Timeout.InfiniteTimeSpan.");
    return milliseconds;
  }

  /// <summary>
  /// Takes the lock if it is free, even while waiters are queued or woken. It records no
  /// owner thread, so an asynchronous caller can take the lock this way.
  /// </summary>
  internal bool TryTake()
  {
    var state = Volatile.Read(ref State);
    while ((state & Held) == 0)
    {
      var seen = Interlocked.CompareExchange(ref State, state | Held, state);
      if (seen == state)
        return true;
      state = seen;
    }
    return false;
  }

  /// <summary>
  /// Spins while the lock is held, up to about 23 microseconds, and takes the lock if it
  /// frees. While waiters sleep, a spinning caller sets the woken flag, so the release
  /// leaves the lock to it instead of waking a waiter. <paramref name="woken"/> tells whether
  /// the caller holds the flag, which taking the lock clears.
  /// </summary>
  bool SpinToTake(ref bool woken)
  {
    var iterations = FirstSpinIterations;
    var spins = 0;
    while (true)
    {
      var state = Volatile.Read(ref State);
      if ((state & Held) == 0)
      {
        var taken = woken ? (state | Held) & ~Woken : state | Held;
        if (Interlocked.CompareExchange(ref State, taken, state) == state)
          return true;
        continue;
      }
      // Releases that hand the lock to waiters never free it, so spinning does not help then.
      // On one processor, the holder runs only once the caller stops.
      if ((state & Starving) != 0 || spins == SpinCount || IsSingleProcessor)
        return false;
      if (!woken && (state & Woken) == 0 && state >= OneWaiter &&
          Interlocked.CompareExchange(ref State, state | Woken, state) == state)
        woken = true;
      Thread.SpinWait(iterations);
      if (iterations < MaximumSpinIterations)
        iterations <<= 1;
      ++spins;
    }
  }

  bool EnterSlow(long timeout)
  {
    var threadId = Environment.CurrentManagedThreadId;
    if (Volatile.Read(ref OwnerThreadId) == threadId)
      throw new LockRecursionException(
          "The thread already holds this lock, so waiting for it would never end. The lock is not " +
          "reentrant: release it before taking it again.");

    var deadline = timeout == Timeout.Infinite ? long.MaxValue : Environment.TickCount64 + timeout;
    var woken = false;
    // A waiter woken from the queue queues again at the front, as it came before the others.
    var requeue = false;
    SyncWaiter waiter = null;
    while (true)
    {
      if (SpinToTake(ref woken))
      {
        OwnerThreadId = threadId;
        return true;
      }
      var remaining = deadline == long.MaxValue
          ? Timeout.Infinite
          : Math.Max(0, deadline - Environment.TickCount64);
      if (remaining == 0)
      {
        if (woken)
          AbandonWake();
        return false;
      }
      if (waiter == null)
      {
        waiter = ThreadWaiter ??= new SyncWaiter();
        waiter.FirstWaitTimestamp = Stopwatch.GetTimestamp();
      }
      lock (this)
      {
        if (TakeOrQueue(waiter, woken, requeue))
        {
          OwnerThreadId = threadId;
          return true;
        }
      }
      if (!waiter.Wait(remaining) && waiter.TryGiveUp())
      {
        Remove(waiter);
        return false;
      }
      if (Volatile.Read(ref waiter.Status) == Granted)
      {
        OwnerThreadId = threadId;
        return true;
      }
      // Woken to compete for the freed lock, with the woken flag.
      woken = true;
      requeue = true;
    }
  }

  ValueTask<HybridLockScope> LockAsyncSlow(CancellationToken cancellationToken)
  {
    // Only a caller that would wait checks, so the check costs the free lock nothing.
    if (Volatile.Read(ref OwnerThreadId) == Environment.CurrentManagedThreadId)
      return ValueTask.FromException<HybridLockScope>(new LockRecursionException(
          "The thread already holds this lock from Enter, TryEnter, Lock or TryLock, so waiting for it " +
          "would never end. Release it before taking it again. Code that awaits while it holds the " +
          "lock takes it with LockAsync."));
    var woken = false;
    if (SpinToTake(ref woken))
      return new(new HybridLockScope(this));
    AsyncWaiter waiter;
    lock (this)
    {
      waiter = FreeAsyncWaiters;
      if (waiter == null)
      {
        waiter = new AsyncWaiter(this);
      }
      else
      {
        FreeAsyncWaiters = waiter.NextFree;
        waiter.NextFree = null;
        --FreeAsyncWaiterCount;
      }
      waiter.FirstWaitTimestamp = Stopwatch.GetTimestamp();
      if (TakeOrQueue(waiter, woken, front: false))
      {
        // The lock was free, and the unused waiter goes back.
        PushFreeAsyncWaiter(waiter);
        return new(new HybridLockScope(this));
      }
    }
    waiter.Watch(cancellationToken);
    return new ValueTask<HybridLockScope>(waiter, waiter.Version);
  }

  void ExitSlow(int state)
  {
    if ((state & Held) == 0)
      throw new SynchronizationLockException(
          "The lock was released more times than it was taken. Call Exit once for each Enter, and " +
          "dispose each lock scope once.");

    // Without the queue lock while no waiter needs a wake: nobody waits, or a woken waiter or
    // a spinning caller competes for the lock already.
    while ((state & Starving) == 0 && (state < OneWaiter || (state & Woken) != 0))
    {
      var seen = Interlocked.CompareExchange(ref State, state & ~Held, state);
      if (seen == state)
        return;
      state = seen;
    }

    Waiter signaled = null;
    lock (this)
    {
      state = Volatile.Read(ref State);
      if ((state & Starving) != 0)
      {
        while (Head != null)
        {
          var waiter = Head;
          Unlink(waiter);
          if (waiter.TryGrant())
          {
            if (Head == null || Stopwatch.GetTimestamp() - waiter.FirstWaitTimestamp <= StarvationTicks)
              Interlocked.And(ref State, ~Starving);
            signaled = waiter;
            break;
          }
        }
        if (signaled == null)
          Interlocked.And(ref State, ~(Held | Starving));
      }
      else
      {
        Interlocked.And(ref State, ~Held);
        // A spinning caller may set the woken flag after this read. A waiter is then woken
        // as well, which costs a wake and loses nothing.
        if ((state & Woken) == 0)
          signaled = WakeFirst();
      }
    }
    // Outside the queue lock, which a woken waiter needs.
    signaled?.Wake();
  }

  /// <summary>
  /// Wakes the first waiter to compete for the freed lock. Called while holding this object.
  /// </summary>
  Waiter WakeFirst()
  {
    while (Head != null)
    {
      var waiter = Head;
      Unlink(waiter);
      // Set before the waiter can see the wake, so the flag is there for it to clear when it
      // takes the lock or queues again.
      Interlocked.Or(ref State, Woken);
      if (waiter.TryWake())
        return waiter;
      Interlocked.And(ref State, ~Woken);
    }
    return null;
  }

  /// <summary>
  /// A caller with the woken flag stops competing. If the lock is free, the next waiter
  /// competes for it.
  /// </summary>
  void AbandonWake()
  {
    Waiter signaled = null;
    lock (this)
    {
      Interlocked.And(ref State, ~Woken);
      if ((Volatile.Read(ref State) & Held) == 0)
        signaled = WakeFirst();
    }
    signaled?.Wake();
  }

  /// <summary>
  /// Takes the lock if it is free, or queues the waiter: at the front if it was woken from
  /// the queue, as it came before the others. A caller with the woken flag clears it either
  /// way. Called while holding this object.
  /// </summary>
  bool TakeOrQueue(Waiter waiter, bool woken, bool front)
  {
    // A waiter that keeps losing the freed lock to running callers makes releases hand it over.
    var starving = front && Stopwatch.GetTimestamp() - waiter.FirstWaitTimestamp > StarvationTicks;
    while (true)
    {
      var state = Volatile.Read(ref State);
      if ((state & Held) == 0)
      {
        var taken = woken ? (state | Held) & ~Woken : state | Held;
        if (Interlocked.CompareExchange(ref State, taken, state) == state)
          return true;
        continue;
      }
      var queued = state + OneWaiter;
      if (woken)
        queued &= ~Woken;
      if (starving)
        queued |= Starving;
      if (Interlocked.CompareExchange(ref State, queued, state) == state)
      {
        // A full fence, so a woken async waiter that queues again then sees a cancellation
        // whose callback found it woken.
        Interlocked.Exchange(ref waiter.Status, Waiting);
        waiter.IsQueued = true;
        if (front)
        {
          waiter.Previous = null;
          waiter.Next = Head;
          if (Head == null)
            Tail = waiter;
          else
            Head.Previous = waiter;
          Head = waiter;
        }
        else
        {
          waiter.Previous = Tail;
          waiter.Next = null;
          if (Tail == null)
            Head = waiter;
          else
            Tail.Next = waiter;
          Tail = waiter;
        }
        return false;
      }
    }
  }

  /// <summary>
  /// Removes the waiter from the queue. Called while holding this object.
  /// </summary>
  void Unlink(Waiter waiter)
  {
    if (waiter.Previous == null)
      Head = waiter.Next;
    else
      waiter.Previous.Next = waiter.Next;
    if (waiter.Next == null)
      Tail = waiter.Previous;
    else
      waiter.Next.Previous = waiter.Previous;
    waiter.Previous = null;
    waiter.Next = null;
    waiter.IsQueued = false;
    Interlocked.Add(ref State, -OneWaiter);
  }

  /// <summary>
  /// Removes a waiter that gave up, unless a release already took it from the queue.
  /// </summary>
  void Remove(Waiter waiter)
  {
    lock (this)
    {
      if (waiter.IsQueued)
        Unlink(waiter);
    }
  }

  void ReturnAsyncWaiter(AsyncWaiter waiter)
  {
    waiter.Reset();
    lock (this)
      PushFreeAsyncWaiter(waiter);
  }

  /// <summary>
  /// Keeps an async waiter for reuse. Called while holding this object.
  /// </summary>
  void PushFreeAsyncWaiter(AsyncWaiter waiter)
  {
    if (FreeAsyncWaiterCount >= MaximumFreeAsyncWaiters)
      return;
    waiter.NextFree = FreeAsyncWaiters;
    FreeAsyncWaiters = waiter;
    ++FreeAsyncWaiterCount;
  }

  abstract class Waiter
  {
    public int Status;

    public long FirstWaitTimestamp;

    public Waiter Previous;

    public Waiter Next;

    public bool IsQueued;

    public bool TryGrant() => Interlocked.CompareExchange(ref Status, Granted, Waiting) == Waiting;

    public bool TryWake() => Interlocked.CompareExchange(ref Status, Signaled, Waiting) == Waiting;

    public bool TryGiveUp() => Interlocked.CompareExchange(ref Status, GivenUp, Waiting) == Waiting;

    /// <summary>
    /// Tells the waiter that it was granted the lock or woken to compete for it.
    /// </summary>
    public abstract void Wake();
  }

  sealed class SyncWaiter : Waiter
  {
    // Set while the thread sleeps, so a wake that comes while it spins needs no pulse.
    int Sleeping;

    public override void Wake()
    {
      // The status changed before this read, so a waiter that does not sleep yet sees the
      // change before it would sleep.
      if (Volatile.Read(ref Sleeping) == 0)
        return;
      lock (this)
        Monitor.Pulse(this);
    }

    /// <summary>
    /// Waits until the waiter is granted the lock or woken, or the timeout passes, and
    /// returns whether it was granted or woken.
    /// </summary>
    public bool Wait(long timeout)
    {
      // A wake often comes within microseconds. Catching it while spinning saves the
      // thread a sleep, and the time it takes to run again.
      var spinner = new SpinWait();
      for (var i = 0; i < WaiterSpinCount; ++i)
      {
        if (Volatile.Read(ref Status) != Waiting)
          return true;
        spinner.SpinOnce(-1);
      }
      lock (this)
      {
        // A full fence, so either this thread sees the status change or the wake sees it
        // sleeping and pulses it. The wake pulses while holding this object, which it gets
        // only once the thread waits.
        Interlocked.Exchange(ref Sleeping, 1);
        try
        {
          if (timeout == Timeout.Infinite)
          {
            while (Volatile.Read(ref Status) == Waiting)
              Monitor.Wait(this);
            return true;
          }
          var deadline = Environment.TickCount64 + timeout;
          while (Volatile.Read(ref Status) == Waiting)
          {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
              return false;
            Monitor.Wait(this, (int)remaining);
          }
          return true;
        }
        finally
        {
          Sleeping = 0;
        }
      }
    }
  }

  // A thread pool work item itself, so competing after a wake allocates nothing.
  sealed class AsyncWaiter : Waiter, IValueTaskSource<HybridLockScope>, IThreadPoolWorkItem
  {
    readonly HybridLock Lock;

    // Continuations run on the thread pool, so a release never runs the next holder's code.
    ManualResetValueTaskSourceCore<bool> Completion = new() { RunContinuationsAsynchronously = true };

    CancellationToken Token;

    CancellationTokenRegistration Registration;

    public AsyncWaiter NextFree;

    public AsyncWaiter(HybridLock hybridLock)
    {
      Lock = hybridLock;
    }

    public short Version => Completion.Version;

    /// <summary>
    /// Gives up the wait when the token is cancelled. Called after the waiter is queued.
    /// </summary>
    public void Watch(CancellationToken token)
    {
      if (!token.CanBeCanceled)
        return;
      Token = token;
      Registration = token.UnsafeRegister(static state => ((AsyncWaiter)state).OnCancelled(), this);
    }

    void OnCancelled()
    {
      // A woken waiter sees the cancellation when it competes, and a granted one keeps the lock.
      if (!TryGiveUp())
        return;
      Lock.Remove(this);
      Completion.SetException(new OperationCanceledException(Token));
    }

    public override void Wake()
    {
      if (Volatile.Read(ref Status) == Granted)
        Completion.SetResult(true);
      else
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }

    /// <summary>
    /// Competes for the freed lock after a wake, with the woken flag, or queues again at the
    /// front.
    /// </summary>
    void IThreadPoolWorkItem.Execute()
    {
      var woken = true;
      if (!Token.IsCancellationRequested && Lock.SpinToTake(ref woken))
      {
        Status = Granted;
        Completion.SetResult(true);
        return;
      }
      var cancelled = false;
      var taken = false;
      lock (Lock)
      {
        // Checked again here, so a cancellation after the check finds the waiter queued.
        if (Token.IsCancellationRequested)
          cancelled = true;
        else
          taken = Lock.TakeOrQueue(this, woken: true, front: true);
      }
      if (cancelled)
      {
        Status = GivenUp;
        Lock.AbandonWake();
        Completion.SetException(new OperationCanceledException(Token));
      }
      else if (taken)
      {
        Status = Granted;
        Completion.SetResult(true);
      }
      // A cancellation callback that ran before the waiter queued again found it woken and
      // left it, so the queued waiter gives up here.
      else if (Token.IsCancellationRequested)
      {
        OnCancelled();
      }
    }

    public HybridLockScope GetResult(short token)
    {
      try
      {
        Completion.GetResult(token);
        return new HybridLockScope(Lock);
      }
      finally
      {
        // Waits for a cancellation callback that is still running, so none runs after reuse.
        Registration.Dispose();
        Registration = default;
        Token = default;
        Lock.ReturnAsyncWaiter(this);
      }
    }

    public ValueTaskSourceStatus GetStatus(short token) => Completion.GetStatus(token);

    public void OnCompleted(
        Action<object> continuation,
        object state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
        => Completion.OnCompleted(continuation, state, token, flags);

    public void Reset()
    {
      Completion.Reset();
      Status = Waiting;
    }
  }
}
