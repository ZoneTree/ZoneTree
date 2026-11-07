using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using ZoneTree.ClusterCrashWorker;
using ZoneTree.Options;

namespace ZoneTree.Cluster.UnitTests;

/// <summary>
/// Kills a process that runs a bank workload over and over on the same data. After each kill the
/// next process checks what survived right after its recovery, and this one checks it at the end:
/// every acknowledged commit that the journal promises, whole transfers in every column, one
/// prefix of the domain's commits, and the state earlier recoveries found.
/// </summary>
[Category("Crash")]
public sealed class ClusterCrashTests
{
  string Root;

  [SetUp]
  public void SetUp()
  {
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "ZoneTree.slnx")))
      root = root.Parent;
    Assert.That(root, Is.Not.Null);
    Root = Path.Combine(root.FullName, ".tmp", "cluster-crash", "tests", Guid.NewGuid().ToString("N"));
  }

  [TearDown]
  public void TearDown()
  {
    // A failed run keeps its data for a look.
    if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Passed &&
        Directory.Exists(Root))
      Directory.Delete(Root, recursive: true);
  }

  [TestCase(WriteAheadLogMode.Shared, 1)]
  [TestCase(WriteAheadLogMode.AsyncShared, 2)]
  public async Task KilledProcessesKeepEveryPromisedCommitAndOnePrefixInEveryColumn(WriteAheadLogMode mode, int seed)
  {
    const int processes = 12;
    var random = new Random(seed);
    var data = Path.Combine(Root, "data");
    var acknowledgements = Path.Combine(Root, "acks");
    Directory.CreateDirectory(acknowledgements);
    var failures = new List<string>();
    var ready = 0;
    for (var process = 0; process < processes && failures.Count == 0; ++process)
    {
      // Every fourth process dies early, often during its recovery.
      var early = process % 4 == 3;
      var (output, wasReady) = await RunAndKillAsync(data, mode, process, acknowledgements, early, random);
      if (wasReady)
        ++ready;
      failures.AddRange(output
          .Where(line => line.StartsWith("FAIL ", StringComparison.Ordinal) || line.StartsWith("ERROR ", StringComparison.Ordinal))
          .Select(line => $"process {process}: {line}"));
    }
    Assert.That(failures, Is.Empty, $"Data in {Root}");
    Assert.That(ready, Is.GreaterThanOrEqualTo(processes * 3 / 4));

    // The last kill, checked here; then the domain commits again and closes cleanly.
    var options = CrashWorkload.Options(data, mode);
    long survivors;
    using (var cluster = Factory.OpenInProcess(options))
    {
      var check = CrashWorkload.Verify(cluster, acknowledgements, mode);
      Assert.That(check.Failures, Is.Empty, $"Data in {Root}");
      Assert.That(check.Seeded, Is.True);
      survivors = check.Survivors.Values.Sum();
      TestContext.Out.WriteLine($"{survivors} transfers survived {processes} kills.");
      Assert.That(CrashWorkload.TryTransfer(cluster.GetDomain(CrashWorkload.Domain),
          processes, 0, 0, 1, 2, 5, 0, durable: false), Is.True);
    }
    using (var cluster = Factory.OpenInProcess(options))
    {
      var check = CrashWorkload.Verify(cluster, acknowledgements, mode);
      Assert.That(check.Failures, Is.Empty, $"Data in {Root}");
      Assert.That(check.Survivors.Values.Sum(), Is.EqualTo(survivors + 1));
    }
  }

  readonly ZoneTreeClusterFactory Factory = new();

  static async Task<(List<string> Output, bool Ready)> RunAndKillAsync(string data, WriteAheadLogMode mode,
      int process, string acknowledgements, bool early, Random random)
  {
    var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
    {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ClusterCrashWorker.dll"));
    start.ArgumentList.Add("run");
    start.ArgumentList.Add(data);
    start.ArgumentList.Add(mode.ToString());
    start.ArgumentList.Add(process.ToString(CultureInfo.InvariantCulture));
    start.ArgumentList.Add(acknowledgements);

    var lines = new ConcurrentQueue<string>();
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var worker = new Process { StartInfo = start };
    worker.OutputDataReceived += (_, e) =>
    {
      if (e.Data == null)
        return;
      lines.Enqueue(e.Data);
      if (e.Data == "READY")
        ready.TrySetResult();
    };
    worker.ErrorDataReceived += (_, e) =>
    {
      if (!string.IsNullOrEmpty(e.Data))
        lines.Enqueue("ERROR " + e.Data);
    };
    worker.Start();
    worker.BeginOutputReadLine();
    worker.BeginErrorReadLine();
    var exited = worker.WaitForExitAsync();
    if (early)
      await Task.WhenAny(Task.Delay(random.Next(0, 800)), exited);
    else
    {
      var first = await Task.WhenAny(ready.Task, exited, Task.Delay(TimeSpan.FromSeconds(60)));
      if (first == ready.Task)
        await Task.WhenAny(Task.Delay(random.Next(200, 900)), exited);
      else if (first != exited)
        lines.Enqueue("ERROR The process was not ready within 60 seconds.");
    }
    if (worker.HasExited)
      lines.Enqueue($"ERROR The process exited with code {worker.ExitCode} before the kill.");
    else
      worker.Kill(entireProcessTree: true);
    await exited;
    // Waits for the end of the redirected output.
    worker.WaitForExit();
    return ([.. lines], ready.Task.IsCompleted);
  }
}
