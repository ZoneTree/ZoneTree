using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ZoneTree.Options;

namespace ZoneTree.WalSoak;

sealed record Manifest(int Version, string RunId, string Build, Options Options, DateTimeOffset Created);
sealed record CaseStatus(string State, DateTimeOffset Utc, string? Detail = null);
sealed record WorkloadOutcome(string Ending, double Seconds, long AcknowledgedCommits);
sealed record Coverage(long NextCase, long Passed, Dictionary<string, long> Profiles, Dictionary<string, long> Endings, HashSet<long> Completed);
sealed class ResourceLimit(string text) : Exception(text);

static class Supervisor
{
  const string ManifestFile = "run.json";
  static long nextStorageCheck;
  static long diskBytes, freeBytes;
  static string Fingerprint() => string.Join("/", new[] { typeof(Supervisor).Assembly.Location, typeof(ZoneTreeFactory<,>).Assembly.Location }.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));

  public static async Task<int> Run(Options options, CancellationToken stop)
  {
    Directory.CreateDirectory(options.Root);
    RejectLinks(options.Root);
    using var ownership = new FileStream(Path.Combine(options.Root, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var manifestPath = Path.Combine(options.Root, ManifestFile);
    Manifest manifest;
    if (options.Resume)
    {
      manifest = Json.Read<Manifest>(manifestPath);
      LaneModel.Require(manifest.Version == 1 && manifest.Build == Fingerprint(), "Resume requires the same model and binaries. Use a new root after rebuilding.");
      // Keep the original workload, seed, and limits. Duration/cycles apply to this invocation.
      options = manifest.Options with { Resume = true, DurationSeconds = options.DurationSeconds, Cycles = options.Cycles };
    }
    else
    {
      LaneModel.Require(!File.Exists(manifestPath) && !Directory.EnumerateFileSystemEntries(options.Root).Any(x => Path.GetFileName(x) != "run.lock"), "Root is not empty. Use --resume or a new dedicated directory.");
      manifest = new(1, Guid.NewGuid().ToString("N"), Fingerprint(), options, DateTimeOffset.UtcNow);
      Json.Save(manifestPath, manifest);
    }
    using var log = new RunLog(options.Root);
    var casesPath = Path.Combine(options.Root, "cases");
    Directory.CreateDirectory(casesPath);
    CaseConfig? active = null;
    long passed = 0;
    var clock = Stopwatch.StartNew();
    try
    {
      log.Write("start", $"Run {manifest.RunId}; seed {options.Seed}; {(options.Resume ? "resuming" : "new")}", new { options, build = manifest.Build, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, cpu = Environment.ProcessorCount });
      var existing = Directory.EnumerateDirectories(casesPath).OrderBy(x => x, StringComparer.Ordinal).ToList();
      var coveragePath = Path.Combine(options.Root, "coverage.json");
      var coverage = File.Exists(coveragePath) ? Json.Read<Coverage>(coveragePath) : new Coverage(0, 0, new(), new(), new());
      var index = coverage.NextCase;
      void RecordPass(CaseConfig config)
      {
        if (!coverage.Completed.Add(config.Index)) return;
        var profile = config.Profile.ToString();
        coverage.Profiles[profile] = coverage.Profiles.GetValueOrDefault(profile) + 1;
        var outcomeFile = Path.Combine(config.Directory, "workload.json");
        var ending = File.Exists(outcomeFile) ? Json.Read<WorkloadOutcome>(outcomeFile).Ending : "supervisor-interruption";
        coverage.Endings[ending] = coverage.Endings.GetValueOrDefault(ending) + 1;
        coverage = coverage with { Passed = coverage.Passed + 1, NextCase = Math.Max(coverage.NextCase, config.Index + 1) };
        Json.Save(coveragePath, coverage);
      }
      foreach (var directory in existing)
      {
        var configFile = Path.Combine(directory, "config.json");
        if (!File.Exists(configFile)) throw new InvalidDataException($"Unrecognized case directory: {directory}");
        var config = Json.Read<CaseConfig>(configFile);
        ValidateCase(config, manifest);
        index = Math.Max(index, config.Index + 1);
        var status = ReadStatus(directory);
        if (status == "failed") throw new InvalidDataException($"Prior failure preserved at {directory}. Inspect it and use a new root for a new run.");
        if (status == "passed") { RecordPass(config); continue; }
        if (status == "interrupted-before-ready") continue;
        active = config;
        await WaitForWorker(config, options.RecoveryTimeoutSeconds, stop);
        var initialized = File.Exists(Path.Combine(directory, "oracle.jsonl")) && File.ReadLines(Path.Combine(directory, "oracle.jsonl")).Any(line => line.Contains("\"type\":\"ready\"", StringComparison.Ordinal));
        if (!initialized)
        {
          SaveStatus(config, "interrupted-before-ready", "Supervisor stopped before initialization was acknowledged; evidence retained.");
          log.Write("interrupted", $"Case {config.Index} did not reach initialization; skipping recovery assertions.");
          active = null;
          continue;
        }
        log.Write("resume", $"Verifying interrupted case {config.Index}.");
        await VerifyCase(config, log, stop, true);
        await RunChecks(config, log, stop);
        SaveStatus(config, "passed", "Recovered after supervisor interruption.");
        RecordPass(config);
        ++passed;
        active = null;
      }
      Prune(options, manifest);
      var profiles = Profile.Matrix(options.Seed);
      var endings = new[] { "random-kill", "before-fsync", "after-fsync", "after-durable-ack", "merge-created", "after-meta-rename", "clean" };
      var completed = 0;
      while (!stop.IsCancellationRequested && clock.Elapsed.TotalSeconds < options.DurationSeconds && (options.Cycles == 0 || completed < options.Cycles))
      {
        GuardResources(options);
        var profile = profiles[(int)(index % profiles.Count)];
        var longRun = options.LongEvery > 0 && (index + 1) % options.LongEvery == 0;
        var ending = longRun ? "clean" : endings[(int)(index % endings.Length)];
        if (longRun) profile = profile with { Workload = "durable" };
        if (profile.Workload == "queued" && ending == "after-durable-ack") ending = "after-fsync";
        if (options.SelfTest)
        {
          profile = new(index % 2 == 0 ? WriteAheadLogMode.Shared : WriteAheadLogMode.AsyncShared,
            new[] { CompressionMethod.None, CompressionMethod.LZ4, CompressionMethod.Zstd, CompressionMethod.Zstd }[(int)(index % 4)],
            index % 2 == 0 ? DiskSegmentMode.SingleDiskSegment : DiskSegmentMode.MultiPartDiskSegment,
            index % 2 == 0 ? 1024 : 128 * 1024, 32 * 1024, 256, index % 3 == 0 ? "prefix" : "mixed");
          ending = new[] { "before-fsync", "after-durable-ack", "random-kill", "clean" }[(int)(index % 4)];
        }
        var directory = Path.Combine(casesPath, index.ToString("D8"));
        Directory.CreateDirectory(directory);
        active = new(manifest.RunId, index, unchecked(options.Seed + (int)index * 7919) & int.MaxValue, directory, profile, options,
          profile.Workload == "prefix" ? 1 : options.Workers, Math.Min(longRun ? options.LongEpochSeconds : options.EpochSeconds, Math.Max(2, options.DurationSeconds - (int)clock.Elapsed.TotalSeconds)), ending);
        Json.Save(Path.Combine(directory, "config.json"), active);
        SaveStatus(active, "running");
        coverage = coverage with { NextCase = index + 1 };
        Json.Save(coveragePath, coverage);
        log.Write("case", $"Case {index}: {profile}; {active.Seconds}s; ending {ending}", active);
        await RunWorkload(active, log, stop);
        SaveStatus(active, "recovering");
        await VerifyCase(active, log, stop, false);
        SaveStatus(active, "checking");
        await RunChecks(active, log, stop);
        SaveStatus(active, "passed");
        RecordPass(active);
        log.Write("passed", $"Case {index} passed recovery and durability checks.");
        ++passed; ++completed; ++index;
        active = null;
        Json.Save(Path.Combine(options.Root, "status.json"), new { state = "running", utc = DateTimeOffset.UtcNow, nextCase = index, passedThisInvocation = passed, elapsedSeconds = clock.Elapsed.TotalSeconds });
        Prune(options, manifest);
      }
      var state = stop.IsCancellationRequested ? "stopped" : "completed";
      Json.Save(Path.Combine(options.Root, "status.json"), new { state, utc = DateTimeOffset.UtcNow, passedThisInvocation = passed, elapsedSeconds = clock.Elapsed.TotalSeconds });
      log.Write(state, $"{passed} cases passed during this invocation; elapsed {clock.Elapsed}.");
      return stop.IsCancellationRequested ? 130 : 0;
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
      if (active != null) SaveStatus(active, "interrupted", "Stop requested; resume to finish verification.");
      Json.Save(Path.Combine(options.Root, "status.json"), new { state = "stopped", utc = DateTimeOffset.UtcNow, @case = active?.Directory });
      log.Write("stopped", "Stop requested. Resume to verify any interrupted case.");
      return 130;
    }
    catch (Exception error)
    {
      if (active != null) SaveStatus(active, "failed", error.ToString());
      log.Write(error is ResourceLimit ? "resource-limit" : "failure", error.Message, new { @case = active?.Directory, error = error.ToString() }, true);
      Json.Save(Path.Combine(options.Root, "status.json"), new { state = "failed", utc = DateTimeOffset.UtcNow, @case = active?.Directory, error = error.ToString() });
      return error is ResourceLimit ? 3 : 1;
    }
  }

  static async Task RunWorkload(CaseConfig config, RunLog log, CancellationToken stop)
  {
    await using var child = new Child("worker-child", config, log);
    var start = Stopwatch.StartNew();
    double? readyAt = null;
    double? stoppingAt = null;
    var killed = false;
    var armed = false;
    double nextProgress = 0;
    while (!child.Process.HasExited)
    {
      child.CheckErrors();
      GuardResources(config.Options, child.Process);
      if (child.Ready && readyAt == null) readyAt = start.Elapsed.TotalSeconds;
      var elapsed = start.Elapsed.TotalSeconds;
      if (child.Ready && elapsed - child.LastCommitSeconds(start) > config.Options.StallSeconds && stoppingAt == null)
        throw new TimeoutException("Writer made no commit progress within the stall timeout.");
      if (!child.Ready && elapsed > config.Options.RecoveryTimeoutSeconds) throw new TimeoutException("Worker initialization timed out.");
      if (child.CrashPoint)
      {
        child.Kill(); killed = true;
        log.Write("kill", $"Case {config.Index}: reached {config.Ending}; killing worker.");
        break;
      }
      if (stop.IsCancellationRequested && stoppingAt == null) { await child.Command("stop"); stoppingAt = elapsed; }
      if (readyAt.HasValue && elapsed - readyAt.Value >= config.Seconds && stoppingAt == null && !armed)
      {
        if (config.Ending == "random-kill") { child.Kill(); killed = true; break; }
        if (config.Ending == "clean") { await child.Command("stop"); stoppingAt = elapsed; }
        else { await child.Command("arm"); armed = true; }
      }
      if (armed && elapsed - readyAt!.Value > config.Seconds + config.Options.StallSeconds)
        throw new TimeoutException($"Crash point {config.Ending} was not reached.");
      if (stoppingAt.HasValue && elapsed - stoppingAt.Value > config.Options.RecoveryTimeoutSeconds) throw new TimeoutException("Worker clean shutdown timed out.");
      if (elapsed >= nextProgress)
      {
        nextProgress = elapsed + config.Options.ProgressSeconds;
        try
        {
          var metric = new { @case = config.Index, profile = config.Profile.ToString(), elapsedSeconds = elapsed, commits = child.Commits, rssMiB = child.Process.WorkingSet64 / 1048576, handles = Handles(child.Process), cpuSeconds = child.Process.TotalProcessorTime.TotalSeconds, runGiB = diskBytes / 1073741824d, freeGiB = freeBytes / 1073741824d, metrics = child.Metrics };
          log.Write("progress", $"Case {config.Index}: {child.Commits} commits, {metric.rssMiB} MiB RSS, {metric.handles} handles.", metric);
          Json.Save(Path.Combine(config.Options.Root, "status.json"), new { state = "running", utc = DateTimeOffset.UtcNow, metric });
        }
        catch (InvalidOperationException) when (child.Process.HasExited) { break; }
        catch (System.ComponentModel.Win32Exception) when (child.Process.HasExited) { break; }
      }
      await Task.Delay(200);
    }
    await child.Finish();
    child.CheckErrors();
    LaneModel.Require(killed || child.Process.ExitCode == 0 && child.Final, $"Worker exited unexpectedly: {child.Process.ExitCode}; final receipt={child.Final}.");
    Json.Save(Path.Combine(config.Directory, "workload.json"), new WorkloadOutcome(killed ? config.Ending : child.Budget ? "budget-clean" : stop.IsCancellationRequested ? "operator-stop" : "clean", start.Elapsed.TotalSeconds, child.Commits));
    log.Write("workload-ended", $"Case {config.Index}: {(killed ? "process killed" : "clean shutdown")}; {child.Commits} acknowledged commits.");
  }

  static async Task RunChecks(CaseConfig config, RunLog log, CancellationToken stop)
  {
    // An interrupted probe directory belongs to an earlier attempt; preserve it.
    var probe = Path.Combine(config.Directory, "probes");
    if (Directory.Exists(probe)) Directory.Move(probe, probe + "-" + Guid.NewGuid().ToString("N"));
    await RunBounded("check-child", config, log, stop);
  }

  static async Task VerifyCase(CaseConfig config, RunLog log, CancellationToken stop, bool resumed)
  {
    var target = Path.Combine(config.Directory, "verification-" + Guid.NewGuid().ToString("N"));
    GuardResources(config.Options);
    var bytes = Size(config.Database);
    var free = new DriveInfo(config.Directory).AvailableFreeSpace;
    if (free - bytes < config.Options.MinFreeGiB * 1073741824L || Size(config.Options.Root) + bytes > config.Options.MaxDiskGiB * 1073741824L)
      throw new ResourceLimit("Not enough budget to preserve the original database and make a verification copy.");
    Copy(config.Database, target, stop);
    await RunBounded("verify-child", config, log, stop, target, resumed ? "resume" : "normal");
  }

  static async Task RunBounded(string command, CaseConfig config, RunLog log, CancellationToken stop, params string[] arguments)
  {
    await using var child = new Child(command, config, log, arguments);
    var clock = Stopwatch.StartNew();
    var next = 0d;
    while (!child.Process.HasExited)
    {
      stop.ThrowIfCancellationRequested();
      child.CheckErrors();
      GuardResources(config.Options, child.Process);
      if (clock.Elapsed.TotalSeconds > config.Options.RecoveryTimeoutSeconds) throw new TimeoutException($"{command} exceeded the recovery timeout.");
      if (clock.Elapsed.TotalSeconds >= next) { next += config.Options.ProgressSeconds; log.Write("checking", $"Case {config.Index}: {command}, {clock.Elapsed.TotalSeconds:F0}s elapsed."); }
      await Task.Delay(250, stop);
    }
    await child.Finish();
    child.CheckErrors();
    LaneModel.Require(child.Process.ExitCode == 0, $"{command} failed with exit code {child.Process.ExitCode}.");
    if (command == "verify-child") LaneModel.Require(child.Verified, "Recovery child omitted its verified result.");
  }

  public static async Task<int> VerifyExisting(string directory, CancellationToken stop)
  {
    var config = Json.Read<CaseConfig>(Path.Combine(directory, "config.json"));
    LaneModel.Require(Path.GetFullPath(config.Directory) == directory, "Case was moved. Restore its original path before verifying.");
    using var ownership = new FileStream(Path.Combine(config.Options.Root, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    using var log = new RunLog(config.Options.Root);
    await WaitForWorker(config, config.Options.RecoveryTimeoutSeconds, stop);
    try { await VerifyCase(config, log, stop, true); log.Write("verified", $"Verified case {config.Index}; original database preserved."); return 0; }
    catch (Exception error) { log.Write("verification-failure", error.Message, error.ToString(), true); return 1; }
  }

  static async Task WaitForWorker(CaseConfig config, int seconds, CancellationToken stop)
  {
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    while (true)
    {
      try { using var handle = new FileStream(Path.Combine(config.Directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); return; }
      catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(500, stop); }
    }
  }
  static void SaveStatus(CaseConfig config, string state, string? detail = null) => Json.Save(Path.Combine(config.Directory, "status.json"), new CaseStatus(state, DateTimeOffset.UtcNow, detail));
  static string? ReadStatus(string directory) => File.Exists(Path.Combine(directory, "status.json")) ? Json.Read<CaseStatus>(Path.Combine(directory, "status.json")).State : null;
  static void ValidateCase(CaseConfig config, Manifest manifest)
  {
    var expected = Path.Combine(manifest.Options.Root, "cases", config.Index.ToString("D8"));
    LaneModel.Require(config.RunId == manifest.RunId && Path.GetFullPath(config.Directory) == expected, "Case ownership or path does not match its run manifest.");
    RejectLinks(expected);
  }
  static void Prune(Options options, Manifest manifest)
  {
    var passed = Directory.EnumerateDirectories(Path.Combine(options.Root, "cases")).Where(x => ReadStatus(x) == "passed").OrderByDescending(x => x, StringComparer.Ordinal).Skip(options.Retain);
    foreach (var directory in passed.ToArray())
    {
      var config = Json.Read<CaseConfig>(Path.Combine(directory, "config.json"));
      ValidateCase(config, manifest);
      RejectTreeLinks(directory);
      Directory.Delete(directory, true); // Only validated, owned, successfully checked cases.
    }
  }
  static void RejectLinks(string path)
  {
    for (DirectoryInfo? folder = new(Path.GetFullPath(path)); folder != null; folder = folder.Parent)
      LaneModel.Require((folder.Attributes & FileAttributes.ReparsePoint) == 0, $"Symlink/reparse directories are not accepted: {folder.FullName}");
  }
  static void RejectTreeLinks(string path)
  {
    foreach (var entry in Directory.EnumerateFileSystemEntries(path))
    {
      var attributes = File.GetAttributes(entry);
      LaneModel.Require((attributes & FileAttributes.ReparsePoint) == 0, $"Symlink/reparse entry is not accepted: {entry}");
      if ((attributes & FileAttributes.Directory) != 0) RejectTreeLinks(entry);
    }
  }
  static long Size(string path)
  {
    if (!Directory.Exists(path)) return 0;
    long size = 0;
    foreach (var entry in Directory.EnumerateFileSystemEntries(path))
    {
      try
      {
        var attributes = File.GetAttributes(entry);
        LaneModel.Require((attributes & FileAttributes.ReparsePoint) == 0, $"Unexpected symlink: {entry}");
        size += (attributes & FileAttributes.Directory) != 0 ? Size(entry) : new FileInfo(entry).Length;
      }
      catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { } // Concurrent WAL reclamation.
    }
    return size;
  }
  static void Copy(string source, string destination, CancellationToken stop)
  {
    RejectTreeLinks(source);
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source)) { stop.ThrowIfCancellationRequested(); File.Copy(file, Path.Combine(destination, Path.GetFileName(file))); }
    foreach (var directory in Directory.EnumerateDirectories(source)) Copy(directory, Path.Combine(destination, Path.GetFileName(directory)), stop);
  }
  static int Handles(Process process)
  {
    try { return OperatingSystem.IsLinux() ? Directory.EnumerateFileSystemEntries($"/proc/{process.Id}/fd").Count() : process.HandleCount; }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { return -1; }
  }
  static void GuardResources(Options options, Process? process = null)
  {
    if (process != null && !process.HasExited)
    {
      try
      {
        process.Refresh();
        if (process.WorkingSet64 > options.MaxRssMiB * 1048576L) throw new ResourceLimit($"Child RSS exceeded {options.MaxRssMiB} MiB.");
        if (Handles(process) > 32768) throw new ResourceLimit("Child exceeded 32768 open handles/file descriptors.");
      }
      catch (InvalidOperationException) when (process.HasExited) { }
      catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }
    if (Environment.TickCount64 < nextStorageCheck) return;
    nextStorageCheck = Environment.TickCount64 + 5000;
    freeBytes = new DriveInfo(options.Root).AvailableFreeSpace;
    diskBytes = Size(options.Root);
    if (freeBytes < options.MinFreeGiB * 1073741824L) throw new ResourceLimit("Minimum free disk space reached.");
    if (diskBytes > options.MaxDiskGiB * 1073741824L) throw new ResourceLimit("Run disk budget exceeded.");
  }

  sealed class Child : IAsyncDisposable
  {
    readonly Task output, errors;
    readonly FileStream? oracle;
    readonly StreamWriter diagnostics;
    string? failure;
    long lastCommitTick = Environment.TickCount64;
    public Process Process { get; }
    public bool Ready, CrashPoint, Final, Verified, Budget;
    public long Commits;
    public Message? Metrics;
    public Child(string command, CaseConfig config, RunLog log, params string[] arguments)
    {
      if (command == "worker-child") oracle = new FileStream(Path.Combine(config.Directory, "oracle.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
      diagnostics = new StreamWriter(new FileStream(Path.Combine(config.Directory, "events.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
      var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
      if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
      info.ArgumentList.Add(command); info.ArgumentList.Add(Path.Combine(config.Directory, "config.json"));
      foreach (var argument in arguments) info.ArgumentList.Add(argument);
      info.Environment["DOTNET_GCHeapHardLimit"] = ((long)config.Options.MaxRssMiB * 1048576 * 3 / 4).ToString("X");
      Process = Process.Start(info) ?? throw new IOException("Could not start soak child.");
      output = Task.Run(async () =>
      {
        while (await Process.StandardOutput.ReadLineAsync() is { } line)
        {
          var message = JsonSerializer.Deserialize<Message>(line, Json.Settings) ?? throw new InvalidDataException("Invalid child message.");
          if (message.Type is "issued" or "commit" or "synced" or "final" or "ready")
          {
            (oracle ?? throw new InvalidDataException("Unexpected receipt from non-writer child.")).Write(System.Text.Encoding.UTF8.GetBytes(line + "\n"));
            oracle.Flush(false);
          }
          else diagnostics.WriteLine(line);
          switch (message.Type)
          {
            case "ready": Volatile.Write(ref Ready, true); Interlocked.Exchange(ref lastCommitTick, Environment.TickCount64); break;
            case "commit": Interlocked.Increment(ref Commits); Interlocked.Exchange(ref lastCommitTick, Environment.TickCount64); break;
            case "crash-point": Volatile.Write(ref CrashPoint, true); break;
            case "final": Volatile.Write(ref Final, true); break;
            case "budget": Volatile.Write(ref Budget, true); break;
            case "verified": Volatile.Write(ref Verified, true); log.Write("recovery", $"Case {config.Index}: recovered sequences [{string.Join(",", message.Sequences ?? [])}].", message.Data); break;
            case "metrics": Volatile.Write(ref Metrics, message); break;
            case "check": log.Write("check", $"Case {config.Index}: {message.Text}"); break;
            case "fatal": case "engine-error": Interlocked.CompareExchange(ref failure, message.Text ?? message.Type, null); break;
          }
        }
      });
      errors = Task.Run(async () =>
      {
        using var writer = new StreamWriter(new FileStream(Path.Combine(config.Directory, command + ".stderr.log"), FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        while (await Process.StandardError.ReadLineAsync() is { } line) { writer.WriteLine(line); Interlocked.CompareExchange(ref failure, "Child stderr: " + line, null); }
      });
    }
    public double LastCommitSeconds(Stopwatch clock) => clock.Elapsed.TotalSeconds - (Environment.TickCount64 - Interlocked.Read(ref lastCommitTick)) / 1000d;
    public async Task Command(string command)
    {
      try { if (!Process.HasExited) { await Process.StandardInput.WriteLineAsync(command); await Process.StandardInput.FlushAsync(); } }
      catch (IOException) when (Process.HasExited) { }
    }
    public void CheckErrors()
    {
      if (output.IsFaulted) output.GetAwaiter().GetResult();
      if (errors.IsFaulted) errors.GetAwaiter().GetResult();
      if (Volatile.Read(ref failure) is { } error) throw new InvalidDataException(error);
    }
    public void Kill() { if (!Process.HasExited) Process.Kill(true); }
    public async Task Finish() { await Process.WaitForExitAsync(); await Task.WhenAll(output, errors); oracle?.Flush(true); }
    public async ValueTask DisposeAsync()
    {
      try { Kill(); await Finish().WaitAsync(TimeSpan.FromSeconds(15)); }
      finally { oracle?.Dispose(); diagnostics.Dispose(); Process.Dispose(); }
    }
  }
}
