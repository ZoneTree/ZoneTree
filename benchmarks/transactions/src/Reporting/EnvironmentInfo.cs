using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Transactions.Benchmark.Reporting;

public static class EnvironmentInfo
{
  public static IReadOnlyList<string> Describe()
  {
    var packages = ReadPackageVersions();
    var tiered = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation");
    return
    [
      $"OS: {RuntimeInformation.OSDescription}",
      $"Architecture: {RuntimeInformation.ProcessArchitecture}",
      $".NET: {Environment.Version}; server GC: {GCSettings.IsServerGC}; DOTNET_TieredCompilation: {(string.IsNullOrEmpty(tiered) ? "unset" : tiered)}",
      $"CPU: {CpuName()}",
      $"Logical processors: {Environment.ProcessorCount}",
      $"Available memory: {Format.Bytes(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)}",
      $"ZoneTree: {ZoneTree.Core.ZoneTreeInfo.ProductVersion}",
      $"RocksDB (RocksDbNative package): {packages.GetValueOrDefault("RocksDbNative", "n/a")}",
      $"SQLite: {SqliteVersion()} (Microsoft.Data.Sqlite {packages.GetValueOrDefault("Microsoft.Data.Sqlite", "n/a")})",
    ];
  }

  static string CpuName()
  {
    if (OperatingSystem.IsWindows())
    {
      var name = Microsoft.Win32.Registry.GetValue(
          @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
          "ProcessorNameString",
          null);
      return name?.ToString()?.Trim() ?? "n/a";
    }

    if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
    {
      var line = File.ReadLines("/proc/cpuinfo")
          .FirstOrDefault(x => x.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
      if (line != null)
        return line[(line.IndexOf(':') + 1)..].Trim();
    }
    return "n/a";
  }

  static string SqliteVersion()
  {
    using var connection = new SqliteConnection("Data Source=:memory:");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT sqlite_version();";
    return command.ExecuteScalar()?.ToString() ?? "n/a";
  }

  static Dictionary<string, string> ReadPackageVersions()
  {
    var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var assembly = Assembly.GetEntryAssembly()?.Location;
    if (string.IsNullOrEmpty(assembly))
      return versions;
    var dependencies = Path.ChangeExtension(assembly, ".deps.json");
    if (!File.Exists(dependencies))
      return versions;

    using var document = JsonDocument.Parse(File.ReadAllText(dependencies));
    foreach (var library in document.RootElement.GetProperty("libraries").EnumerateObject())
    {
      var separator = library.Name.LastIndexOf('/');
      if (separator > 0)
        versions[library.Name[..separator]] = library.Name[(separator + 1)..];
    }
    return versions;
  }
}
