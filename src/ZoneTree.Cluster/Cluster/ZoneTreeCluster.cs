using System.Collections.Concurrent;
using System.Globalization;
using ZoneTree.Transactional;
using ZoneTree.Backup;
using ZoneTree.Core;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.AbstractFileStream;
using ZoneTree.Options;
using ArgumentOutOfRangeException = System.ArgumentOutOfRangeException;

namespace ZoneTree.Cluster;

/// <summary>An in-process cluster with coordinated, standalone and raw columns.</summary>
/// <remarks>
/// The catalog is the source of truth for the schema. A column opens when it is declared or
/// created in this process, or when it is first requested with its types.
/// The transactions of a domain commit atomically across its coordinated columns.
/// </remarks>
internal sealed class ZoneTreeCluster : IZoneTreeClusterHost, IZoneTreeClusterMaintenance
{
  readonly FileStream Lease;
  // Guards opening and creating columns and domains, the maintainers, and the settings that
  // newly opened columns receive.
  readonly Lock SchemaLock = new();
  readonly ConcurrentDictionary<long, Column> OpenColumns = new();
  readonly ConcurrentDictionary<long, ClusterDomain> Domains = new();
  readonly ConcurrentDictionary<ClusterLiveBackup, byte> LiveBackups = new();
  readonly object BackupGate = new();
  int RunningBackups;
  readonly List<ClusterMaintainer> Maintainers = [];
  // The writing standalone transactions that disposal rolls back if they are still active.
  // Domains find their writing transactions in their own registries.
  readonly ConcurrentDictionary<IClusterOpenTransaction, byte> OpenTransactions = new();
  int Disposed;
  bool readOnly;
  TimeSpan autoSyncInterval;

  internal ClusterCatalog Catalog { get; }
  readonly TimestampReservationWorker Reservations;
  internal ClusterEncodingRegistry Encodings { get; }
  internal IColumnStorageConfigurator StorageDefaults { get; }

  /// <summary>The file streams of every column's storage.</summary>
  internal IFileStreamProvider FileStreamProvider { get; }

  /// <summary>The write ahead log settings every coordinated column uses.</summary>
  internal WriteAheadLogOptions CoordinatedWriteAheadLog { get; }

  /// <summary>The settings that prepare the columns' storage.</summary>
  internal ColumnStorageSettings StorageSettings { get; }
  internal string DataDirectory { get; }
  public ILogger Logger { get; }
  public IClusterSchema Schema { get; }

  internal ZoneTreeCluster(string directory, ZoneTreeClusterOptions options, FileStream lease, ClusterCatalog catalog)
  {
    DataDirectory = directory;
    Logger = options.Logger;
    Lease = lease;
    Catalog = catalog;
    Reservations = new TimestampReservationWorker(catalog, Logger);
    Encodings = options.Encodings;
    StorageDefaults = options.StorageDefaults;
    FileStreamProvider = options.FileStreamProvider ?? new LocalFileStreamProvider();
    CoordinatedWriteAheadLog = CopyOf(options.CoordinatedWriteAheadLog);
    StorageSettings = new ColumnStorageSettings(Logger, FileStreamProvider, CoordinatedWriteAheadLog, StorageDefaults);
    Schema = new ClusterSchema(this);
    autoSyncInterval = options.AutoSyncInterval;
    StartDomains();
    PurgeDroppedColumns();
  }

  /// <summary>
  /// Finishes the drops a crash interrupted: deletes the storage of each dropped column, then
  /// its record.
  /// </summary>
  void PurgeDroppedColumns()
  {
    foreach (var column in Catalog.State.DroppedColumns)
    {
      DeleteDirectory(ColumnDirectory(column.Id));
      Catalog.PurgeColumn(column.Id);
    }
  }

  /// <summary>
  /// Starts every domain's clock after the bound it reserved before, in one durable catalog
  /// commit that reserves the next batches. A domain that was not closed cleanly recovers
  /// first, before any of its columns opens: see <see cref="DomainRecovery"/>.
  /// </summary>
  void StartDomains()
  {
    foreach (var (domain, clockStart, wasClean) in Catalog.OpenDomains())
    {
      var record = domain;
      if (!wasClean)
        record = Catalog.RecordRecovery(domain.Id, DomainRecovery.FindCut(this, domain, clockStart), clockStart);
      Domains[domain.Id] = CreateDomain(record, clockStart);
    }
  }

  ClusterDomain CreateDomain(DomainRecord record, long clockStart)
  {
    var clock = new DomainTimestampSource(Reservations, record.Id, clockStart, record.TimestampBound);
    var coordinator = new DomainCoordinator(this, record.Id, clock, clockStart);
    return new ClusterDomain(this, record, coordinator);
  }

  /// <summary>The domain of a coordinated column.</summary>
  internal ClusterDomain DomainOf(ColumnRecord record) => Domains[record.DomainId];

  /// <summary>
  /// Applies the declared schema: creates the missing domains and columns, checks the existing
  /// ones and opens the declared columns. Nothing is deleted.
  /// </summary>
  internal void ApplyDeclarations(ZoneTreeClusterOptions options,
      IReadOnlyList<(ColumnDefinition Column, string Domain)> columns)
  {
    foreach (var domain in options.Domains ?? [])
      GetOrCreateDomain(domain.Name, getOrCreate: true);
    foreach (var (column, domain) in columns)
      column.GetOrCreate(this, domain);
    // Read-only applies after the declarations, so a read-only cluster can still be created.
    IsReadOnly = options.IsReadOnly;
  }

  /// <summary>A copy of the settings, so later changes to the caller's instance do not apply.</summary>
  internal static WriteAheadLogOptions CopyOf(WriteAheadLogOptions options) => new()
  {
    WriteAheadLogMode = options.WriteAheadLogMode,
    CompressionBlockSize = options.CompressionBlockSize,
    CompressionMethod = options.CompressionMethod,
    CompressionLevel = options.CompressionLevel,
    SharedModeOptions = new() { MaximumChunkSize = options.SharedModeOptions.MaximumChunkSize }
  };

  /// <summary>
  /// Whether two write ahead log settings make the same journal: its mode, which sets what a
  /// crash can lose, and the block format recovery reads. The compression level and the chunk
  /// size only change how new records are written.
  /// </summary>
  internal static bool SameJournal(WriteAheadLogOptions left, WriteAheadLogOptions right)
      => left.WriteAheadLogMode == right.WriteAheadLogMode &&
         left.CompressionBlockSize == right.CompressionBlockSize &&
         left.CompressionMethod == right.CompressionMethod;

  internal string ColumnDirectory(long id) => ColumnDirectory(DataDirectory, id);

  internal static string ColumnDirectory(string dataDirectory, long id)
      => Path.Combine(dataDirectory, "columns", id.ToString(CultureInfo.InvariantCulture));

  public IClusterDomain GetDomain(string name)
  {
    ThrowIfDisposed();
    return FindDomain(name) ?? throw new KeyNotFoundException($"Domain '{name}' does not exist.");
  }

  ClusterDomain FindDomain(string name)
  {
    var record = Catalog.State.FindDomain(name);
    if (record == null)
      return null;
    if (Domains.TryGetValue(record.Id, out var domain))
      return domain;
    // The domain is in the catalog, and its creation finishes under the schema lock.
    lock (SchemaLock)
      return Domains.TryGetValue(record.Id, out domain) ? domain : null;
  }

  internal ClusterDomain GetOrCreateDomain(string name, bool getOrCreate)
  {
    lock (SchemaLock)
    {
      ThrowIfDisposed();
      var existing = FindDomain(name);
      if (existing != null)
        return getOrCreate ? existing : throw new SchemaObjectAlreadyExistsException($"Domain '{name}' already exists.");
      ThrowIfReadOnly();
      var record = Catalog.CreateDomain(name);
      var domain = CreateDomain(record, clockStart: 0);
      Domains[record.Id] = domain;
      return domain;
    }
  }

  /// <summary>
  /// Drops a column. The catalog drops it at once, so its name is free and its handles reject
  /// new uses. Its running operations finish, and its views, transactions and iterators end.
  /// Then its storage is deleted and its record purged. Returns false if no column has the name.
  /// </summary>
  internal bool DropColumn(string name)
  {
    ColumnRecord record;
    Column column;
    ClusterMaintainer[] maintainers;
    lock (SchemaLock)
    {
      ThrowIfDisposed();
      record = Catalog.State.FindColumn(name);
      if (record == null)
        return false;
      ThrowIfReadOnly();
      Catalog.DropColumn(record.Id);
      if (OpenColumns.TryGetValue(record.Id, out column))
        OpenColumns.TryRemove(record.Id, out _);
      column?.MarkDropped();
      maintainers = Maintainers.ToArray();
    }
    // Nothing can reach the column any more, so deleting it needs no schema lock. A crash
    // from here on leaves a dropped record, which the next open purges. A running backup
    // generation may still read the storage, so the deletion waits for it.
    WaitForBackupGenerations();
    if (column != null)
    {
      foreach (var maintainer in maintainers)
      {
        try { maintainer.Remove(column); }
        catch (Exception error) { Logger.LogError(error); }
      }
      if (!MakeDurableBeforeDeleting(record))
      {
        // The next open recovers with the column's commit records, then purges it.
        column.Dispose();
        return true;
      }
      column.DropStorage(Logger);
    }
    else if (!MakeDurableBeforeDeleting(record))
      return true;
    DeleteDirectory(ColumnDirectory(record.Id));
    Catalog.PurgeColumn(record.Id);
    return true;
  }

  /// <summary>
  /// Recovery cannot read the commit records of a deleted coordinated column, so before its
  /// storage goes, every commit it can be part of becomes durable and the domain records that
  /// in the catalog: recovery counts those commits as complete. Returns false if that failed;
  /// the column then stays until the next open.
  /// </summary>
  bool MakeDurableBeforeDeleting(ColumnRecord record)
  {
    if (record.Kind != ColumnKind.Coordinated || !Domains.TryGetValue(record.DomainId, out var domain))
      return true;
    try
    {
      var durable = domain.Coordinator.MakeIssuedCommitsDurable();
      Catalog.SetDomainDurable(record.DomainId, durable, isClean: false);
      return true;
    }
    catch (Exception error)
    {
      Logger.LogError(error);
      return false;
    }
  }

  /// <summary>Renames a column. Its handles, views and transactions are not affected.</summary>
  internal void RenameColumn(string name, string newName)
  {
    lock (SchemaLock)
    {
      ThrowIfDisposed();
      var record = Catalog.State.FindColumn(name)
          ?? throw new KeyNotFoundException($"Column '{name}' does not exist.");
      ThrowIfReadOnly();
      Catalog.RenameColumn(record.Id, newName);
      if (OpenColumns.TryGetValue(record.Id, out var column))
        column.Rename(newName);
    }
  }

  /// <summary>
  /// Drops a domain without columns, so its handle rejects new uses. Returns false if no
  /// domain has the name.
  /// </summary>
  /// <exception cref="SchemaConflictException">The domain has columns.</exception>
  internal bool DropDomain(string name)
  {
    lock (SchemaLock)
    {
      ThrowIfDisposed();
      var record = Catalog.State.FindDomain(name);
      if (record == null)
        return false;
      ThrowIfReadOnly();
      Catalog.DropDomain(record.Id);
      if (Domains.TryRemove(record.Id, out var domain))
      {
        domain.MarkDropped();
        domain.Coordinator.Shutdown(clean: false);
      }
      return true;
    }
  }

  /// <summary>Renames a domain. Its handle, columns and transactions are not affected.</summary>
  internal void RenameDomain(string name, string newName)
  {
    lock (SchemaLock)
    {
      ThrowIfDisposed();
      var record = Catalog.State.FindDomain(name)
          ?? throw new KeyNotFoundException($"Domain '{name}' does not exist.");
      ThrowIfReadOnly();
      Catalog.RenameDomain(record.Id, newName);
      if (Domains.TryGetValue(record.Id, out var domain))
        domain.Rename(newName);
    }
  }

  public IRawColumn<TKey, TValue> GetRawColumn<TKey, TValue>(string name)
      => GetColumn<TKey, TValue>(name, ColumnKind.Raw, null) as IRawColumn<TKey, TValue>
          ?? throw TypeMismatch(name);

  public IStandaloneColumn<TKey, TValue> GetStandaloneColumn<TKey, TValue>(string name)
      => GetColumn<TKey, TValue>(name, ColumnKind.Standalone, null) as IStandaloneColumn<TKey, TValue>
          ?? throw TypeMismatch(name);

  internal ICoordinatedColumn<TKey, TValue> GetCoordinatedColumn<TKey, TValue>(ClusterDomain domain, string name)
      => GetColumn<TKey, TValue>(name, ColumnKind.Coordinated, domain) as ICoordinatedColumn<TKey, TValue>
          ?? throw TypeMismatch(name);

  static InvalidOperationException TypeMismatch(string name)
      => new($"Column '{name}' has a different kind, key type or value type.");

  /// <summary>
  /// Gets a column of the catalog, opening it with these types if this process has not
  /// opened it yet. A domain restricts the lookup to its columns.
  /// </summary>
  Column GetColumn<TKey, TValue>(string name, ColumnKind kind, ClusterDomain domain)
  {
    ThrowIfDisposed();
    var record = Catalog.State.FindColumn(name);
    if (record == null || (domain != null && record.DomainId != domain.Id))
      throw new KeyNotFoundException(domain == null
          ? $"Column '{name}' does not exist."
          : $"Domain '{domain.Name}' has no column '{name}'.");
    if (record.Kind != kind || !Encodings.CanEncode<TKey, TValue>(record))
      throw TypeMismatch(name);
    if (OpenColumns.TryGetValue(record.Id, out var column))
      return column;
    lock (SchemaLock)
    {
      ThrowIfDisposed();
      if (OpenColumns.TryGetValue(record.Id, out column))
        return column;
      record = Catalog.State.FindColumn(name);
      var definition = DefinitionOf<TKey, TValue>(record);
      return OpenColumnLocked(definition, record, Encodings.Resolve<TKey, TValue>(definition), factory: null);
    }
  }

  /// <summary>The definition that opens a catalog column requested with its types.</summary>
  static ColumnDefinition DefinitionOf<TKey, TValue>(ColumnRecord record) => record.Kind switch
  {
    ColumnKind.Coordinated => new CoordinatedColumnDefinition<TKey, TValue>(record.Name)
    {
      KeySerializerId = record.KeySerializer.Id,
      ValueSerializerId = record.ValueSerializer.Id,
      ComparerId = record.Comparer.Id
    },
    ColumnKind.Standalone => new StandaloneColumnDefinition<TKey, TValue>(record.Name)
    {
      KeySerializerId = record.KeySerializer.Id,
      ValueSerializerId = record.ValueSerializer.Id,
      ComparerId = record.Comparer.Id
    },
    _ => new RawColumnDefinition<TKey, TValue>(record.Name)
    {
      KeySerializerId = record.KeySerializer.Id,
      ValueSerializerId = record.ValueSerializer.Id,
      ComparerId = record.Comparer.Id
    },
  };

  /// <summary>
  /// Opens a column of the catalog, or adds it first. Called for declared columns and for
  /// columns created through the schema API.
  /// </summary>
  internal Column GetOrCreateColumn<TKey, TValue>(ColumnDefinition definition, string domainName, bool getOrCreate)
  {
    ArgumentNullException.ThrowIfNull(definition);
    var encodings = Encodings.Resolve<TKey, TValue>(definition);
    lock (SchemaLock)
    {
      ThrowIfDisposed();
      long domainId = 0;
      if (definition.Kind == ColumnKind.Coordinated)
      {
        if (domainName == null)
          throw new ArgumentException($"Coordinated column '{definition.Name}' needs a domain.", nameof(domainName));
        domainId = (FindDomain(domainName)
            ?? throw new KeyNotFoundException($"Domain '{domainName}' does not exist.")).Id;
      }
      else if (domainName != null)
        throw new ArgumentException(
            $"Column '{definition.Name}' is a {definition.Kind} column; only coordinated columns belong to a domain.",
            nameof(domainName));
      var existing = Catalog.State.FindColumn(definition.Name);
      if (existing != null)
      {
        if (!getOrCreate)
          throw new SchemaObjectAlreadyExistsException($"Column '{definition.Name}' already exists.");
        if (existing.Kind != definition.Kind)
          throw new SchemaConflictException(
              $"Column '{definition.Name}' exists as a {existing.Kind} column, not a {definition.Kind} column.");
        if (existing.DomainId != domainId)
          throw new SchemaConflictException(
              $"Column '{definition.Name}' exists in {DescribeDomain(existing.DomainId)}, not in {DescribeDomain(domainId)}.");
        if (!encodings.Matches(existing))
          throw new SchemaConflictException(
              $"Column '{definition.Name}' exists with {encodings.DescribeDifference(existing)}.");
        return OpenColumns.TryGetValue(existing.Id, out var open)
            ? open
            : OpenColumnLocked(definition, existing, encodings, factory: null);
      }
      ThrowIfReadOnly();
      // The storage hooks run before the column enters the catalog, so a failing hook
      // leaves no column behind.
      var factory = ColumnSchema.Prepare(StorageSettings, definition, encodings,
          ((ITypedColumnDefinition<TKey, TValue>)definition).ConfigureStorage);
      // Ids only grow, and a drop deletes the storage before its record, so storage under the
      // next id belongs to a column the catalog lost.
      if (FileStreamProvider.DirectoryExists(ColumnDirectory(Catalog.State.Cluster.NextColumnId)))
        throw new ClusterCatalogCorruptionException(
            $"Column storage exists under the id that column '{definition.Name}' would take.");
      var record = Catalog.CreateColumn(definition.Name, definition.Kind, domainId,
          encodings.KeySerializerEncoding, encodings.ValueSerializerEncoding, encodings.ComparerEncoding,
          typeof(TKey).SimplifiedFullName(), typeof(TValue).SimplifiedFullName());
      return OpenColumnLocked(definition, record, encodings, factory);
    }
  }

  string DescribeDomain(long id)
      => id == 0 ? "no domain" : $"domain '{Catalog.State.FindDomain(id)?.Name}'";

  /// <summary>
  /// Opens a catalog column under the schema lock, with a prepared storage factory or a new
  /// one. The column's storage is created first if the catalog says it may not exist yet;
  /// the catalog then records it as ready.
  /// </summary>
  /// <exception cref="ColumnEncodingException">
  /// The registered encodings are not the ones the column was created with.
  /// </exception>
  Column OpenColumnLocked<TKey, TValue>(ColumnDefinition definition, ColumnRecord record,
      ColumnEncodings<TKey, TValue> encodings, ZoneTreeFactory<TKey, TValue> factory)
  {
    if (!encodings.Matches(record))
      throw new ColumnEncodingException(
          $"Column '{record.Name}' was created with {encodings.DescribeDifference(record)}.");
    var typed = (ITypedColumnDefinition<TKey, TValue>)definition;
    factory ??= ColumnSchema.Prepare(StorageSettings, definition, encodings, typed.ConfigureStorage);
    factory.SetDataDirectory(ColumnDirectory(record.Id));
    var column = typed.OpenStorage(this, record, factory);
    try
    {
      if (record.State == ColumnState.Creating)
        Catalog.MarkColumnReady(record.Id);
      if (record.Kind == ColumnKind.Coordinated)
        column.Domain = DomainOf(record);
      column.AutoSyncInterval = autoSyncInterval;
      column.SetReadOnly(readOnly);
    }
    catch
    {
      column.Dispose();
      throw;
    }
    OpenColumns[record.Id] = column;
    // The column is open and in use, so a maintainer that cannot attach it is only logged.
    foreach (var maintainer in Maintainers)
    {
      try { maintainer.Add(column); }
      catch (Exception error) { Logger.LogError(error); }
    }
    return column;
  }

  /// <summary>The columns of a domain that are open in this process.</summary>
  internal IEnumerable<Column> OpenColumnsOf(ClusterDomain domain)
      => OpenColumns.Values.Where(column => column.Domain == domain);

  public Task SyncWALAsync(CancellationToken cancellationToken = default)
  {
    ThrowIfDisposed();
    cancellationToken.ThrowIfCancellationRequested();
    // Columns not opened in this process have no writes to sync. A domain syncs its
    // coordinated columns together.
    var sync = Task.WhenAll(OpenColumns.Values
        .Where(column => column.Domain == null)
        .Select(column => column.SyncWALAsync())
        .Concat(Domains.Values.Select(domain => domain.Coordinator.SyncAsync())));
    return cancellationToken.CanBeCanceled ? sync.WaitAsync(cancellationToken) : sync;
  }

  public TimeSpan AutoSyncInterval
  {
    get => autoSyncInterval;
    set
    {
      ThrowIfDisposed();
      ValidateSyncInterval(value);
      lock (SchemaLock)
      {
        foreach (var column in OpenColumns.Values)
          column.AutoSyncInterval = value;
        autoSyncInterval = value;
      }
    }
  }

  internal static void ValidateSyncInterval(TimeSpan interval)
  {
    if (interval != TimeSpan.Zero && (interval.TotalMilliseconds < 1 || interval.TotalMilliseconds >= uint.MaxValue))
      throw new ArgumentOutOfRangeException(nameof(interval), interval, "Invalid background sync interval.");
  }

  public bool IsReadOnly
  {
    get => Volatile.Read(ref readOnly);
    set
    {
      ThrowIfDisposed();
      lock (SchemaLock)
      {
        foreach (var column in OpenColumns.Values)
          column.SetReadOnly(value);
        Volatile.Write(ref readOnly, value);
      }
    }
  }

  void ThrowIfReadOnly()
  {
    if (IsReadOnly)
      throw new ZoneTreeIsReadOnlyException();
  }

  public IZoneTreeClusterMaintenance Maintenance => this;

  IReadOnlyList<string> IZoneTreeClusterMaintenance.ColumnNames
      => Catalog.State.Columns.Select(column => column.Name).ToArray();

  IReadOnlyList<string> IZoneTreeClusterMaintenance.DomainNames
      => Catalog.State.Domains.Select(domain => domain.Name).ToArray();

  // Domains count the transactions of their columns, and standalone columns count their own.
  public int ActiveTransactionCount
      => OpenColumns.Values.Sum(column => column.ActiveTransactionCount) +
          Domains.Values.Sum(domain => domain.Coordinator.Snapshots.ActiveCount);
  public int AbortedTransactionCount => OpenColumns.Values.Sum(column => column.AbortedTransactionCount);
  public int ConflictIndexCount => OpenColumns.Values.Sum(column => column.ConflictIndexCount);

  internal void TransactionStarted(IClusterOpenTransaction transaction) => OpenTransactions.TryAdd(transaction, 0);
  internal void TransactionEnded(IClusterOpenTransaction transaction) => OpenTransactions.TryRemove(transaction, out _);

  void RollBackOpenTransactions(ref List<Exception> errors)
  {
    // Read-only domain transactions hold nothing to roll back; their later calls fail.
    var transactions = OpenTransactions.Keys
        .Concat(Domains.Values.SelectMany(domain => domain.Coordinator.Snapshots.GetActiveWriters()));
    foreach (var transaction in transactions)
    {
      try { transaction.RollbackOnClose(); }
      catch (Exception error) { (errors ??= []).Add(error); }
    }
  }

  public IMaintainer CreateMaintainer()
  {
    lock (SchemaLock)
    {
      ThrowIfDisposed();
      var maintainer = new ClusterMaintainer(this, OpenColumns.Values.OrderBy(column => column.Id), followsNewColumns: true);
      Maintainers.Add(maintainer);
      return maintainer;
    }
  }

  internal void RemoveMaintainer(ClusterMaintainer maintainer)
  {
    lock (SchemaLock)
      Maintainers.Remove(maintainer);
  }

  public IMaintainer CreateColumnMaintainer(string name)
  {
    ThrowIfDisposed();
    var record = Catalog.State.FindColumn(name)
        ?? throw new KeyNotFoundException($"Column '{name}' does not exist.");
    lock (SchemaLock)
    {
      ThrowIfDisposed();
      if (!OpenColumns.TryGetValue(record.Id, out var column))
        throw new InvalidOperationException(
            $"Column '{name}' is not open in this process. Get its typed handle to open it.");
      // A maintainer of one column, which leaves it when the column is dropped.
      var maintainer = new ClusterMaintainer(this, [column], followsNewColumns: false);
      Maintainers.Add(maintainer);
      return maintainer;
    }
  }

  public ITransactionalZoneTreeMaintenance<TKey, TValue> GetCoordinatedColumnMaintenance<TKey, TValue>(string name)
      => (GetColumn<TKey, TValue>(name, ColumnKind.Coordinated, null) as CoordinatedColumn<TKey, TValue>
          ?? throw TypeMismatch(name)).Maintenance;

  public IZoneTreeMaintenance<TKey, TValue> GetRawColumnMaintenance<TKey, TValue>(string name)
      => (GetColumn<TKey, TValue>(name, ColumnKind.Raw, null) as RawColumn<TKey, TValue>
          ?? throw TypeMismatch(name)).Maintenance;

  public ITransactionalZoneTree<TKey, TValue> GetStandaloneColumnStorage<TKey, TValue>(string name)
      => (GetColumn<TKey, TValue>(name, ColumnKind.Standalone, null) as StandaloneColumn<TKey, TValue>
          ?? throw TypeMismatch(name)).Tree;

  public void SaveMetaData()
  {
    ThrowIfDisposed();
    foreach (var column in OpenColumns.Values)
      column.SaveMetaData();
  }

  public void Drop()
  {
    if (Interlocked.Exchange(ref Disposed, 1) != 0)
      ObjectDisposedException.ThrowIf(true, this);
    // The dropped data goes away with the transactions, so a failed rollback is only logged.
    List<Exception> errors = null;
    DisposeLiveBackups(ref errors);
    RollBackOpenTransactions(ref errors);
    errors?.ForEach(Logger.LogError);
    try
    {
      foreach (var domain in Domains.Values)
        domain.Coordinator.Shutdown(clean: false);
      lock (SchemaLock)
      {
        foreach (var column in OpenColumns.Values)
          column.DropStorage(Logger);
      }
      Reservations.Dispose();
      Catalog.Drop();
      // The columns this process did not open have no open files.
      DeleteDirectory(Path.Combine(DataDirectory, "columns"));
      DeleteDirectory(Path.Combine(DataDirectory, "catalog"));
    }
    finally
    {
      Lease.Dispose();
    }
    File.Delete(Path.Combine(DataDirectory, "database.lock"));
  }

  void DeleteDirectory(string path)
  {
    if (FileStreamProvider.DirectoryExists(path))
      FileStreamProvider.DeleteDirectory(path, recursive: true);
  }

  public void Dispose()
  {
    if (Interlocked.Exchange(ref Disposed, 1) != 0)
      return;
    List<Exception> errors = null;
    // A running backup generation finishes while the columns are open.
    DisposeLiveBackups(ref errors);
    RollBackOpenTransactions(ref errors);
    // The domains sync their columns and record a clean shutdown while the columns are open.
    foreach (var domain in Domains.Values)
    {
      try { domain.Coordinator.Shutdown(clean: true); }
      catch (Exception error) { (errors ??= []).Add(error); }
    }
    lock (SchemaLock)
    {
      foreach (var column in OpenColumns.Values)
      {
        try { column.Dispose(); }
        catch (Exception error) { (errors ??= []).Add(error); }
      }
    }
    // The worker stops before the catalog closes, so no reservation runs against a closed catalog.
    Reservations.Dispose();
    try { Catalog.Dispose(); }
    catch (Exception error) { (errors ??= []).Add(error); }
    Lease.Dispose();
    if (errors != null)
      throw new AggregateException(errors);
  }

  public ValueTask DisposeAsync()
  {
    Dispose();
    return ValueTask.CompletedTask;
  }

  internal void ThrowIfDisposed()
  {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref Disposed) != 0, this);
  }

  public ILiveBackup CreateLiveBackup(LocalLiveBackupOptions storage, ClusterLiveBackupOptions options = null)
      => AddLiveBackup(ClusterBackupLocation.Local(storage), options);

  public ILiveBackup CreateLiveBackup(IBackupObjectStorage storage,
      ObjectStorageLiveBackupOptions storageOptions = null, ClusterLiveBackupOptions options = null)
      => AddLiveBackup(ClusterBackupLocation.ObjectStorage(storage, storageOptions), options);

  ClusterLiveBackup AddLiveBackup(ClusterBackupLocation location, ClusterLiveBackupOptions options)
  {
    ThrowIfDisposed();
    var backup = new ClusterLiveBackup(this, location, options);
    LiveBackups[backup] = 0;
    return backup;
  }

  internal void RemoveLiveBackup(ClusterLiveBackup backup) => LiveBackups.TryRemove(backup, out _);

  /// <summary>Stops the live backups and waits for their running generations.</summary>
  void DisposeLiveBackups(ref List<Exception> errors)
  {
    foreach (var backup in LiveBackups.Keys.ToArray())
    {
      try { backup.Dispose(); }
      catch (Exception error) { (errors ??= []).Add(error); }
    }
  }

  internal void OnColumnMergeSucceeded()
  {
    foreach (var backup in LiveBackups.Keys)
      backup.OnColumnMergeSucceeded();
  }

  /// <summary>
  /// Begins a cluster backup generation. Under the schema lock, it pins each domain's cut,
  /// creates every column's backup, which captures the coordinated columns while the cuts are
  /// pinned, and reads the catalog. Drops wait until the generation is disposed.
  /// </summary>
  /// <exception cref="InvalidOperationException">A column is not open in this process.</exception>
  internal ClusterBackupGeneration BeginBackupGeneration(
      Func<Column, int, ClusterBackupPublishing, LiveBackupOptions> optionsFor)
  {
    lock (SchemaLock)
    {
      ThrowIfDisposed();
      // A column opens with its types, which only its definition knows.
      var closed = Catalog.State.Columns
          .Where(column => column.State == ColumnState.Ready && !OpenColumns.ContainsKey(column.Id))
          .Select(column => column.Name)
          .ToArray();
      if (closed.Length > 0)
        throw new InvalidOperationException(
            $"A live backup needs every column open. Declare or open these columns first: {string.Join(", ", closed)}.");
      var columns = OpenColumns.Values.OrderBy(column => column.Id).ToArray();
      var publishing = new ClusterBackupPublishing(columns.Length);
      var backups = new List<ColumnBackup>(columns.Length);
      var readers = new List<SnapshotBucket>(Domains.Count);
      var points = new Dictionary<long, CommitPoint>();
      var oldestWriters = new Dictionary<long, long>();
      lock (BackupGate)
        ++RunningBackups;
      try
      {
        foreach (var domain in Domains.Values)
        {
          var (reader, point, oldestWriter) =
              domain.Coordinator.Sequencer.JoinReaderAtSnapshot(domain.Coordinator.Snapshots);
          readers.Add(reader);
          points[domain.Id] = point;
          oldestWriters[domain.Id] = oldestWriter;
        }
        foreach (var column in columns)
          backups.Add(column.CreateBackup(optionsFor(column, columns.Length, publishing),
              column.Domain != null ? oldestWriters[column.Domain.Id] : long.MaxValue));
      }
      catch
      {
        foreach (var backup in backups)
          backup.Dispose();
        EndBackupGeneration();
        throw;
      }
      finally
      {
        // The captures hold what the cuts read, so merges may go on from here.
        foreach (var reader in readers)
          reader.Leave();
      }
      // Read after the captures, so the bounds cover every timestamp they hold.
      var catalog = Catalog.State;
      var cuts = catalog.Domains
          .Select(domain => new ClusterBackupCut(domain.Id,
              points.TryGetValue(domain.Id, out var point) ? point.Sequence : domain.DurableSequence,
              domain.TimestampBound))
          .ToArray();
      return new ClusterBackupGeneration(publishing, backups, catalog, cuts, EndBackupGeneration);
    }
  }

  void EndBackupGeneration()
  {
    lock (BackupGate)
    {
      --RunningBackups;
      Monitor.PulseAll(BackupGate);
    }
  }

  /// <summary>Waits until no backup generation reads the columns' storage.</summary>
  void WaitForBackupGenerations()
  {
    lock (BackupGate)
    {
      while (RunningBackups > 0)
        Monitor.Wait(BackupGate);
    }
  }
}
