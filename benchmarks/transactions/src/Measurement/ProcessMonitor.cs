using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Transactions.Benchmark.Measurement;

public readonly record struct ProcessSnapshot(
    TimeSpan CpuTime,
    TimeSpan GcPause,
    long? ReadBytes,
    long? WriteBytes);

/// <summary>
/// Samples the peak working set, and reads the process counters native engines also count in.
/// </summary>
public sealed partial class ProcessMonitor : IDisposable
{
  static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);

  readonly CancellationTokenSource Stop = new();
  readonly Thread Sampler;
  readonly Process Current = Process.GetCurrentProcess();
  long Peak;

  public long PeakWorkingSetBytes => Interlocked.Read(ref Peak);

  public ProcessMonitor()
  {
    Sampler = new Thread(SampleLoop) { IsBackground = true, Name = "process-monitor" };
    Sampler.Start();
  }

  public void ResetPeak() => Interlocked.Exchange(ref Peak, 0);

  public ProcessSnapshot Snapshot()
  {
    Current.Refresh();
    var (read, write) = ReadIoCounters(Current);
    return new ProcessSnapshot(Current.TotalProcessorTime, GC.GetTotalPauseDuration(), read, write);
  }

  void SampleLoop()
  {
    using var sampled = Process.GetCurrentProcess();
    while (!Stop.IsCancellationRequested)
    {
      sampled.Refresh();
      var workingSet = sampled.WorkingSet64;
      var current = Interlocked.Read(ref Peak);
      while (workingSet > current)
      {
        var seen = Interlocked.CompareExchange(ref Peak, workingSet, current);
        if (seen == current)
          break;
        current = seen;
      }
      Stop.Token.WaitHandle.WaitOne(SampleInterval);
    }
  }

  static (long? Read, long? Write) ReadIoCounters(Process process)
  {
    if (OperatingSystem.IsWindows())
    {
      return GetProcessIoCounters(process.Handle, out var counters)
          ? ((long)counters.ReadTransferCount, (long)counters.WriteTransferCount)
          : (null, null);
    }

    if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/io"))
      return (null, null);

    long? read = null;
    long? write = null;
    foreach (var line in File.ReadLines("/proc/self/io"))
    {
      if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
        read = long.Parse(line["read_bytes:".Length..], CultureInfo.InvariantCulture);
      else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
        write = long.Parse(line["write_bytes:".Length..], CultureInfo.InvariantCulture);
    }
    return (read, write);
  }

  public void Dispose()
  {
    Stop.Cancel();
    Sampler.Join();
    Stop.Dispose();
    Current.Dispose();
  }

  [StructLayout(LayoutKind.Sequential)]
  struct IoCounters
  {
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  static extern bool GetProcessIoCounters(nint process, out IoCounters counters);
}
