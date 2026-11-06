using System.Runtime.CompilerServices;
using ZoneTree.Backup;

namespace ZoneTree.Cluster.UnitTests;

/// <summary>An in-memory object storage for live backups, whose writes can be made to fail.</summary>
public sealed class MemoryBackupObjectStorage : IBackupObjectStorage
{
  readonly object Gate = new();
  readonly Dictionary<string, (byte[] Bytes, string Version)> Objects = new(StringComparer.Ordinal);
  long NextVersion;

  /// <summary>Makes the commit of a matching object fail.</summary>
  public Func<string, bool> FailCreate { get; set; }

  public string[] Keys
  {
    get
    {
      lock (Gate)
        return Objects.Keys.Order(StringComparer.Ordinal).ToArray();
    }
  }

  public ValueTask<BackupObjectRead> TryOpenReadAsync(string key, CancellationToken cancellationToken)
  {
    lock (Gate)
    {
      return ValueTask.FromResult<BackupObjectRead>(Objects.TryGetValue(key, out var entry)
          ? new Read(new BackupObjectInfo(key, entry.Bytes.Length, entry.Version), entry.Bytes)
          : null);
    }
  }

  public ValueTask<BackupObjectInfo> GetInfoAsync(string key, CancellationToken cancellationToken)
  {
    lock (Gate)
    {
      return ValueTask.FromResult(Objects.TryGetValue(key, out var entry)
          ? new BackupObjectInfo(key, entry.Bytes.Length, entry.Version)
          : null);
    }
  }

  public ValueTask<BackupObjectWrite> CreateAsync(string key, CancellationToken cancellationToken)
      => ValueTask.FromResult<BackupObjectWrite>(new Write(this, key));

  public ValueTask<BackupObjectInfo> CompareExchangeAsync(string key, ReadOnlyMemory<byte> content,
      string expectedVersion, CancellationToken cancellationToken)
  {
    lock (Gate)
    {
      Objects.TryGetValue(key, out var previous);
      if (previous.Version != expectedVersion)
        return ValueTask.FromResult<BackupObjectInfo>(null);
      var version = (++NextVersion).ToString(System.Globalization.CultureInfo.InvariantCulture);
      Objects[key] = (content.ToArray(), version);
      return ValueTask.FromResult(new BackupObjectInfo(key, content.Length, version));
    }
  }

  public async IAsyncEnumerable<BackupObjectInfo> ListAsync(string prefix,
      [EnumeratorCancellation] CancellationToken cancellationToken)
  {
    BackupObjectInfo[] listed;
    lock (Gate)
    {
      listed = Objects.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
          .Select(pair => new BackupObjectInfo(pair.Key, pair.Value.Bytes.Length, pair.Value.Version))
          .ToArray();
    }
    foreach (var info in listed)
    {
      await Task.Yield();
      yield return info;
    }
  }

  public ValueTask DeleteAsync(string key, CancellationToken cancellationToken)
  {
    lock (Gate)
      Objects.Remove(key);
    return ValueTask.CompletedTask;
  }

  BackupObjectInfo Commit(string key, byte[] bytes)
  {
    if (FailCreate?.Invoke(key) == true)
      throw new IOException($"Simulated failure writing {key}.");
    lock (Gate)
    {
      if (Objects.ContainsKey(key))
        throw new IOException($"Object {key} already exists.");
      var version = (++NextVersion).ToString(System.Globalization.CultureInfo.InvariantCulture);
      Objects.Add(key, (bytes, version));
      return new BackupObjectInfo(key, bytes.Length, version);
    }
  }

  sealed class Write(MemoryBackupObjectStorage storage, string key) : BackupObjectWrite
  {
    readonly MemoryStream Buffer = new();
    public override Stream Stream => Buffer;
    public override ValueTask<BackupObjectInfo> CommitAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(storage.Commit(key, Buffer.ToArray()));
    public override ValueTask DisposeAsync()
    {
      Buffer.Dispose();
      return ValueTask.CompletedTask;
    }
  }

  sealed class Read(BackupObjectInfo info, byte[] bytes) : BackupObjectRead
  {
    readonly MemoryStream Content = new(bytes, writable: false);
    public override BackupObjectInfo Info => info;
    public override Stream Stream => Content;
    public override ValueTask DisposeAsync()
    {
      Content.Dispose();
      return ValueTask.CompletedTask;
    }
  }
}
