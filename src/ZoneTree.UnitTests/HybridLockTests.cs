using ZoneTree.Threading;

namespace ZoneTree.UnitTests;

public sealed class HybridLockTests
{
  [Test]
  public async Task TheLockExcludesSyncAndAsyncCallers()
  {
    var hybridLock = new HybridLock();
    hybridLock.Enter();
    Assert.That(Task.Run(() => hybridLock.TryEnter(TimeSpan.Zero)).Result, Is.False);
    hybridLock.Exit();
    using (hybridLock.Lock())
      Assert.That(Task.Run(() => hybridLock.TryLock(TimeSpan.Zero, out _)).Result, Is.False);
    Assert.That(hybridLock.TryLock(TimeSpan.Zero, out var scope), Is.True);
    scope.Dispose();
    // A zero timeout does not wait, so the holder only learns that the lock is taken.
    using (await hybridLock.LockAsync())
      Assert.That(hybridLock.TryEnter(TimeSpan.Zero), Is.False);

    // An async waiter gets the lock once a sync holder releases it.
    var held = hybridLock.Lock();
    var waiting = Task.Run(() => hybridLock.LockAsync().AsTask());
    Assert.That(waiting.Wait(50), Is.False);
    held.Dispose();
    Assert.That(waiting.Wait(TimeSpan.FromSeconds(10)), Is.True);
    waiting.Result.Dispose();
    Assert.That(hybridLock.TryEnter(TimeSpan.Zero), Is.True);
    hybridLock.Exit();
  }

  [Test]
  public void AnotherThreadCanReleaseTheLock()
  {
    var hybridLock = new HybridLock();
    hybridLock.Enter();
    Task.Run(hybridLock.Exit).Wait();
    Assert.That(Task.Run(() => hybridLock.TryEnter(TimeSpan.Zero)).Result, Is.True);
    hybridLock.Exit();
  }

  [Test]
  public void RecursionAndExtraReleasesTellWhatToDo()
  {
    var hybridLock = new HybridLock();
    using (hybridLock.Lock())
    {
      var sync = Assert.Throws<LockRecursionException>(hybridLock.Enter);
      Assert.That(sync.Message, Does.Contain("release it before taking it again"));
      var pending = hybridLock.LockAsync();
      Assert.That(pending.IsFaulted, Is.True);
      Assert.That(() => pending.AsTask().GetAwaiter().GetResult(),
          Throws.TypeOf<LockRecursionException>().With.Message.Contains("takes it with LockAsync"));
    }
    var extra = Assert.Throws<SynchronizationLockException>(hybridLock.Exit);
    Assert.That(extra.Message, Does.Contain("Call Exit once for each Enter"));
  }

  [Test]
  public void ACancelledCallTakesNothing()
  {
    var hybridLock = new HybridLock();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Assert.That(hybridLock.LockAsync(cancellation.Token).IsCanceled, Is.True);
    Assert.That(hybridLock.TryEnter(TimeSpan.Zero), Is.True);
    hybridLock.Exit();
  }
}
