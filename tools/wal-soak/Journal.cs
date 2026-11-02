using System.Text;
using System.Text.Json;
using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;

namespace ZoneTree.WalSoak;

sealed record Message(string Type, int Lane = -1, long Sequence = -1, bool Durable = false, long[]? Sequences = null, string? Text = null, string? Phase = null, object? Data = null, long Timestamp = 0);

static class Json
{
  public static readonly JsonSerializerOptions Settings = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
  public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Settings);
  public static T Read<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(file), Settings) ?? throw new InvalidDataException($"Empty JSON: {file}");
  public static void Save<T>(string path, T value)
  {
    var files = new LocalFileStreamProvider();
    var bytes = Encoding.UTF8.GetBytes(Encode(value));
    if (File.Exists(path)) files.GetDurableFileWriter().WriteAllBytes(path, bytes);
    else files.GetDurableFileWriter().WriteNewFile(path, bytes);
    files.SyncDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
  }
}

sealed class Wire : ILogger
{
  readonly object sync = new();
  public LogLevel LogLevel { get; set; } = LogLevel.Warning;
  public void Send(Message message)
  {
    lock (sync) { Console.Out.WriteLine(Json.Encode(message)); Console.Out.Flush(); }
  }
  public void LogError(Exception error) => Send(new(error.ToString().Contains("soak injected", StringComparison.Ordinal) ? "expected-error" : "engine-error", Text: error.ToString()));
  public void LogWarning(object message) => Send(new("warning", Text: message.ToString()));
  public void LogInfo(object message) { }
  public void LogTrace(object message) { }
}

sealed class RunLog : IDisposable
{
  readonly string directory;
  readonly object sync = new();
  public RunLog(string root) { directory = Path.Combine(root, "logs"); System.IO.Directory.CreateDirectory(directory); }
  public void Write(string type, string text, object? data = null, bool failure = false)
  {
    lock (sync)
    {
      var entry = new { utc = DateTimeOffset.UtcNow, type, text, data };
      var path = Path.Combine(directory, failure ? "failures.jsonl" : "progress.jsonl");
      if (!failure && File.Exists(path) && new FileInfo(path).Length >= 16 * 1024 * 1024)
      {
        for (var i = 7; i >= 1; --i)
        {
          var source = i == 1 ? path : path + "." + (i - 1);
          var destination = path + "." + i;
          if (File.Exists(source)) File.Move(source, destination, true);
        }
      }
      using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
      {
        stream.Write(Encoding.UTF8.GetBytes(Json.Encode(entry) + "\n"));
        stream.Flush(failure);
      }
      if (failure)
      {
        using var file = new FileStream(Path.Combine(directory, "failures.log"), FileMode.Append, FileAccess.Write, FileShare.Read);
        file.Write(Encoding.UTF8.GetBytes($"{DateTimeOffset.UtcNow:O} {type}: {text}\n{Json.Encode(data)}\n\n"));
        file.Flush(true);
      }
      Console.WriteLine($"{DateTimeOffset.UtcNow:O} {type}: {text}");
    }
  }
  public void Dispose() { }
}

sealed class Receipts(int lanes)
{
  public long[] Issued { get; } = new long[lanes];
  public long[] Committed { get; } = new long[lanes];
  public long[] Durable { get; } = new long[lanes];
  public void Apply(Message message)
  {
    if (message.Type is "issued" or "commit")
    {
      if ((uint)message.Lane >= lanes || message.Sequence < 1) throw new InvalidDataException("Invalid oracle receipt.");
      if (message.Type == "issued")
      {
        LaneModel.Require(message.Sequence == Issued[message.Lane] + 1, "Nonconsecutive issued receipt.");
        Issued[message.Lane] = message.Sequence;
      }
      else
      {
        LaneModel.Require(message.Sequence == Committed[message.Lane] + 1 && message.Sequence <= Issued[message.Lane], "Nonconsecutive commit receipt.");
        LaneModel.Require(message.Timestamp > 0, "Commit receipt omitted its commit timestamp.");
        Committed[message.Lane] = message.Sequence;
        if (message.Durable) Durable[message.Lane] = Math.Max(Durable[message.Lane], message.Sequence);
      }
    }
    if (message.Type is "synced" or "final")
    {
      if (message.Sequences?.Length != lanes) throw new InvalidDataException("Wrong sync receipt lane count.");
      for (var i = 0; i < lanes; ++i)
      {
        // A completed writer can publish its counter just before its commit receipt
        // reaches stdout. The sync still covers it; issued is the safe upper bound.
        LaneModel.Require(message.Sequences[i] >= 0 && message.Sequences[i] <= Issued[i], "Sync receipt exceeds issued operations.");
        Durable[i] = Math.Max(Durable[i], message.Sequences[i]);
      }
    }
  }
  public static Receipts Load(string file, int lanes)
  {
    var receipts = new Receipts(lanes);
    if (!File.Exists(file)) return receipts;
    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream);
    while (reader.ReadLine() is { } line)
    {
      try { receipts.Apply(JsonSerializer.Deserialize<Message>(line, Json.Settings) ?? throw new JsonException()); }
      catch (JsonException) when (reader.EndOfStream) { /* Supervisor crash can tear the final receipt. */ }
    }
    return receipts;
  }

  public static void CheckGlobalPrefix(string file, long[] recovered)
  {
    long latestKept = 0, earliestLost = long.MaxValue;
    using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
    while (reader.ReadLine() is { } line)
    {
      Message? message;
      try { message = JsonSerializer.Deserialize<Message>(line, Json.Settings); }
      catch (JsonException) when (reader.EndOfStream) { break; }
      if (message?.Type != "commit") continue;
      if (message.Sequence <= recovered[message.Lane]) latestKept = Math.Max(latestKept, message.Timestamp);
      else earliestLost = Math.Min(earliestLost, message.Timestamp);
    }
    LaneModel.Require(latestKept < earliestLost, $"Recovery has a global commit-order hole: kept timestamp {latestKept}, lost timestamp {earliestLost}.");
  }
}
