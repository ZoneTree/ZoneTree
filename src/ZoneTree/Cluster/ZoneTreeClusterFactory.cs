using ZoneTree.Backup;
using ZoneTree.Exceptions;

namespace ZoneTree.Cluster;

/// <summary>Creates and opens clusters whose columns run in this process.</summary>
public sealed class ZoneTreeClusterFactory : IZoneTreeClusterFactory
{
  public IZoneTreeCluster CreateInProcess(ZoneTreeClusterOptions options) => Open(options, true, false);
  public IZoneTreeCluster OpenInProcess(ZoneTreeClusterOptions options) => Open(options, false, true);
  public IZoneTreeCluster OpenOrCreateInProcess(ZoneTreeClusterOptions options) => Open(options, true, true);

  static ZoneTreeCluster Open(ZoneTreeClusterOptions options, bool canCreate, bool canOpen)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(options.Logger);
    if (string.IsNullOrWhiteSpace(options.DataDirectory))
      throw new ArgumentException("A data directory is required.", nameof(options));
    ZoneTreeCluster.ValidateSyncInterval(options.AutoSyncInterval);
    var (definitions, domainNames) = GetColumnDefinitions(options);
    var directory = Path.GetFullPath(options.DataDirectory);
    if (!canCreate && !Directory.Exists(directory))
      throw new DatabaseNotFoundException();
    Directory.CreateDirectory(directory);
    var lease = new FileStream(Path.Combine(directory, "database.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var columns = new Column[definitions.Length];
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
        ReadSchema(manifest, definitions, domainNames);
      var cluster = new ZoneTreeCluster(directory, options.Logger, lease);
      for (; opened < columns.Length; ++opened)
        columns[opened] = definitions[opened].Open(cluster, opened, create: !exists, options.StorageDefaults);
      if (!exists)
        WriteSchema(manifest, definitions, domainNames);
      var domains = options.Domains
          .Select(domain => (domain.Name, columns.Where((_, index) => domainNames[index] == domain.Name).ToArray()))
          .ToArray();
      cluster.Initialize(columns, domains, options);
      return cluster;
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

  // TODO: make a better schema for the cluster before first release to avoid frequent migrations and never try introduce compatibility until we release. always keep schema version 1 until the first release.
  const int SchemaVersion = 1;

  /// <summary>
  /// Validates the options and lists every column in schema declaration order: the columns
  /// of each domain, then the standalone and raw columns. Each column has its domain name,
  /// which is empty for the columns outside domains.
  /// </summary>
  static (ColumnDefinition[] Definitions, string[] DomainNames) GetColumnDefinitions(ZoneTreeClusterOptions options)
  {
    var definitions = new List<ColumnDefinition>();
    var domainNames = new List<string>();
    var domains = new HashSet<string>(StringComparer.Ordinal);
    foreach (var domain in options.Domains ?? [])
    {
      if (domain == null || !domains.Add(domain.Name))
        throw new ArgumentException("Domain definitions must be non-null and have unique names.", nameof(options));
      if (domain.Columns == null || domain.Columns.Count == 0)
        throw new ArgumentException($"Domain '{domain.Name}' needs at least one column.", nameof(options));
      foreach (var column in domain.Columns)
      {
        definitions.Add(column);
        domainNames.Add(domain.Name);
      }
    }
    foreach (var column in options.Columns ?? [])
    {
      if (column?.Kind == ColumnKind.Coordinated)
        throw new ArgumentException($"Coordinated column '{column.Name}' must be declared in a domain.", nameof(options));
      definitions.Add(column);
      domainNames.Add(string.Empty);
    }
    if (definitions.Count == 0)
      throw new ArgumentException("At least one column is required.", nameof(options));
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var definition in definitions)
    {
      if (definition == null || !names.Add(definition.Name))
        throw new ArgumentException("Column definitions must be non-null and have unique names.", nameof(options));
    }
    return (definitions.ToArray(), domainNames.ToArray());
  }

  static void WriteSchema(string path, ColumnDefinition[] definitions, string[] domainNames)
  {
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
    writer.Write(SchemaVersion);
    writer.Write(definitions.Length);
    for (var index = 0; index < definitions.Length; ++index)
    {
      var column = definitions[index];
      writer.Write(column.Name);
      writer.Write(column.KeyType.FullName);
      writer.Write(column.ValueType.FullName);
      writer.Write((byte)column.Kind);
      writer.Write(domainNames[index]);
    }
    writer.Flush();
    stream.Flush(flushToDisk: true);
  }

  static void ReadSchema(string path, ColumnDefinition[] definitions, string[] domainNames)
  {
    using var stream = File.OpenRead(path);
    using var reader = new BinaryReader(stream);
    if (reader.ReadInt32() != SchemaVersion || reader.ReadInt32() != definitions.Length)
      throw new InvalidDataException("The cluster schema does not match the column definitions.");
    for (var index = 0; index < definitions.Length; ++index)
    {
      var column = definitions[index];
      if (reader.ReadString() != column.Name || reader.ReadString() != column.KeyType.FullName ||
          reader.ReadString() != column.ValueType.FullName)
        throw new InvalidDataException("Column names, types and declaration order must match the stored schema.");
      var kind = (ColumnKind)reader.ReadByte();
      if (kind != column.Kind)
        throw new InvalidDataException($"Column '{column.Name}' is stored as a {kind} column, not a {column.Kind} column.");
      var domain = reader.ReadString();
      if (domain != domainNames[index])
        throw new InvalidDataException($"Column '{column.Name}' is stored in another domain.");
    }
    if (stream.Position != stream.Length)
      throw new InvalidDataException("Unexpected data in the cluster schema.");
  }

  public ValueTask<IZoneTreeCluster> RestoreInProcessAsync(ZoneTreeClusterOptions options,
      LocalLiveBackupOptions backup, long? generationId = null, CancellationToken cancellationToken = default)
      => throw new NotSupportedException("Cluster-wide backup and restore are not supported yet.");

  public ValueTask<IZoneTreeCluster> RestoreInProcessAsync(ZoneTreeClusterOptions options,
      IBackupObjectStorage storage, ObjectStorageLiveBackupOptions backupOptions = null,
      long? generationId = null, CancellationToken cancellationToken = default)
      => throw new NotSupportedException("Cluster-wide backup and restore are not supported yet.");
}
