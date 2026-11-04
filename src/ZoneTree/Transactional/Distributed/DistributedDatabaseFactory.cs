using ZoneTree.Backup;
using ZoneTree.Exceptions;

namespace ZoneTree.Transactional.Distributed;

/// <summary>Creates and opens the initial in-process implementation.</summary>
public sealed class DistributedDatabaseFactory : IDistributedDatabaseFactory
{
  public IDistributedDatabase CreateInProcess(DistributedDatabaseOptions options) => Open(options, true, false);
  public IDistributedDatabase OpenInProcess(DistributedDatabaseOptions options) => Open(options, false, true);
  public IDistributedDatabase OpenOrCreateInProcess(DistributedDatabaseOptions options) => Open(options, true, true);

  static DistributedDatabase Open(DistributedDatabaseOptions options, bool canCreate, bool canOpen)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(options.Logger);
    if (string.IsNullOrWhiteSpace(options.DataDirectory))
      throw new ArgumentException("A data directory is required.", nameof(options));
    if (options.Columns == null || options.Columns.Count == 0)
      throw new ArgumentException("At least one column is required.", nameof(options));
    DistributedDatabase.ValidateSyncInterval(options.AutoSyncInterval);
    var definitions = options.Columns.ToArray();
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var definition in definitions)
    {
      if (definition == null || !names.Add(definition.Name))
        throw new ArgumentException("Column definitions must be non-null and have unique names.", nameof(options));
    }
    var directory = Path.GetFullPath(options.DataDirectory);
    if (!canCreate && !Directory.Exists(directory))
      throw new DatabaseNotFoundException();
    Directory.CreateDirectory(directory);
    var lease = new FileStream(Path.Combine(directory, "database.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var columns = new DistributedDatabaseColumn[definitions.Length];
    var opened = 0;
    try
    {
      var manifest = Path.Combine(directory, "database.schema");
      var exists = File.Exists(manifest);
      if (exists && !canOpen)
        throw new DatabaseAlreadyExistsException();
      if (!exists && !canCreate)
        throw new DatabaseNotFoundException();
      if (exists)
        ReadSchema(manifest, definitions);
      var database = new DistributedDatabase(directory, options.Logger, lease);
      for (; opened < columns.Length; ++opened)
        columns[opened] = definitions[opened].Open(database, opened, create: !exists);
      if (!exists)
        WriteSchema(manifest, definitions);
      database.Initialize(columns, options);
      return database;
    }
    catch
    {
      for (var index = opened - 1; index >= 0; --index)
      {
        try { columns[index].Dispose(); }
        catch (Exception error) { options.Logger.LogError(error); }
      }
      lease.Dispose();
      throw;
    }
  }

  static void WriteSchema(string path, DistributedColumnDefinition[] definitions)
  {
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
    writer.Write(1);
    writer.Write(definitions.Length);
    foreach (var column in definitions)
    {
      writer.Write(column.Name);
      writer.Write(column.KeyType.FullName);
      writer.Write(column.ValueType.FullName);
    }
    writer.Flush();
    stream.Flush(flushToDisk: true);
  }

  static void ReadSchema(string path, DistributedColumnDefinition[] definitions)
  {
    using var stream = File.OpenRead(path);
    using var reader = new BinaryReader(stream);
    if (reader.ReadInt32() != 1 || reader.ReadInt32() != definitions.Length)
      throw new InvalidDataException("The database schema does not match the column definitions.");
    foreach (var column in definitions)
    {
      if (reader.ReadString() != column.Name || reader.ReadString() != column.KeyType.FullName ||
          reader.ReadString() != column.ValueType.FullName)
        throw new InvalidDataException("Column names, types and declaration order must match the stored schema.");
    }
    if (stream.Position != stream.Length)
      throw new InvalidDataException("Unexpected data in the database schema.");
  }

  public ValueTask<IDistributedDatabase> RestoreInProcessAsync(DistributedDatabaseOptions options,
      LocalLiveBackupOptions backup, long? generationId = null, CancellationToken cancellationToken = default)
      => throw new NotSupportedException("Database-wide backup and restore are not part of the first slice.");

  public ValueTask<IDistributedDatabase> RestoreInProcessAsync(DistributedDatabaseOptions options,
      IBackupObjectStorage storage, ObjectStorageLiveBackupOptions backupOptions = null,
      long? generationId = null, CancellationToken cancellationToken = default)
      => throw new NotSupportedException("Database-wide backup and restore are not part of the first slice.");
}
