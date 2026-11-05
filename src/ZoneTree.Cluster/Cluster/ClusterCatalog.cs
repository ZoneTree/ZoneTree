using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Transactional;

namespace ZoneTree.Cluster;

/// <summary>
/// The cluster catalog: the single source of truth for the cluster's domains, columns, ids
/// and durable domain state.
/// </summary>
/// <remarks>
/// The catalog is a transactional ZoneTree in the cluster's catalog directory. Each change
/// is one transaction committed durably before it takes effect, and changes run one at a
/// time. Readers use the immutable <see cref="State"/>, which each change replaces.
/// </remarks>
internal sealed class ClusterCatalog : IDisposable
{
  /// <summary>The transaction ids and commit timestamps a domain reserves at a time.</summary>
  internal const long TimestampBatch = 1L << 20;

  const int MaximumNameLength = 256;

  readonly ITransactionalZoneTree<string, Memory<byte>> Tree;
  readonly Lock WriteLock = new();
  volatile CatalogState state;

  internal CatalogState State => state;

  /// <summary>True if opening the catalog created it.</summary>
  internal bool Created { get; }

  ClusterCatalog(ITransactionalZoneTree<string, Memory<byte>> tree, CatalogState state, bool created)
  {
    Tree = tree;
    this.state = state;
    Created = created;
  }

  /// <summary>
  /// Opens the catalog of a cluster directory, or creates it.
  /// </summary>
  /// <exception cref="DatabaseNotFoundException">The catalog does not exist and cannot be created.</exception>
  /// <exception cref="DatabaseAlreadyExistsException">The catalog exists and cannot be opened.</exception>
  internal static ClusterCatalog Open(string clusterDirectory, ILogger logger, bool canCreate, bool canOpen)
  {
    var path = Path.Combine(clusterDirectory, "catalog");
    if (!Directory.Exists(path) && !canCreate)
      throw new DatabaseNotFoundException();
    var tree = new ZoneTreeFactory<string, Memory<byte>>()
        .SetDataDirectory(path)
        .SetLogger(logger)
        .OpenOrCreateTransactional();
    try
    {
      // A catalog without its cluster record was never created: a crash interrupted it.
      // TODO: Known edge case. Our writes never leave domain or column records without the
      // cluster record, but storage damage or an incomplete restore can. Such a catalog is
      // treated as new here, so its ids restart and reuse existing columns' ids and
      // directories, and a failed declaration would then drop the existing data. A new
      // catalog next to an existing columns directory has the same effect. The fix is to
      // refuse both, and to refuse creating a column whose directory already exists.
      var loaded = Load(tree);
      var created = loaded == null;
      if (created)
      {
        if (!canCreate)
          throw new DatabaseNotFoundException();
        var cluster = new ClusterRecord(Guid.NewGuid(), SchemaEpoch: 0, NextDomainId: 1, NextColumnId: 1);
        Commit(tree, transaction => transaction.Upsert(CatalogRecords.ClusterKey, CatalogRecords.Encode(cluster)));
        loaded = new CatalogState(cluster, [], []);
      }
      else if (!canOpen)
        throw new DatabaseAlreadyExistsException();
      return new ClusterCatalog(tree, loaded, created);
    }
    catch
    {
      tree.Dispose();
      throw;
    }
  }

  static CatalogState Load(ITransactionalZoneTree<string, Memory<byte>> tree)
  {
    using var read = tree.BeginReadOnlyTransaction();
    if (!read.TryGet(CatalogRecords.ClusterKey, out var header))
      return null;
    var domains = ReadPrefix(read, CatalogRecords.DomainPrefix, CatalogRecords.DecodeDomain);
    var columns = ReadPrefix(read, CatalogRecords.ColumnPrefix, CatalogRecords.DecodeColumn);
    return new CatalogState(CatalogRecords.DecodeCluster(header), domains, columns);
  }

  /// <summary>Decodes the records whose keys start with a prefix, in key order.</summary>
  static List<T> ReadPrefix<T>(IReadTransaction<string, Memory<byte>> read, string prefix,
      Func<Memory<byte>, T> decode)
  {
    var records = new List<T>();
    using var iterator = read.CreateIterator();
    iterator.Seek(prefix);
    while (iterator.Next() && iterator.CurrentKey.StartsWith(prefix, StringComparison.Ordinal))
      records.Add(decode(iterator.CurrentValue));
    return records;
  }

  /// <summary>Adds a domain with its first timestamp reservation.</summary>
  /// <exception cref="SchemaObjectAlreadyExistsException">The domain exists.</exception>
  internal DomainRecord CreateDomain(string name)
  {
    ValidateName(name, "domain");
    lock (WriteLock)
    {
      var current = state;
      if (current.FindDomain(name) != null)
        throw new SchemaObjectAlreadyExistsException($"Domain '{name}' already exists.");
      var domain = new DomainRecord(current.Cluster.NextDomainId, name, TimestampBatch);
      var cluster = current.Cluster with
      {
        SchemaEpoch = current.Cluster.SchemaEpoch + 1,
        NextDomainId = current.Cluster.NextDomainId + 1
      };
      Commit(Tree, transaction =>
      {
        transaction.Upsert(CatalogRecords.ClusterKey, CatalogRecords.Encode(cluster));
        transaction.Upsert(CatalogRecords.DomainKey(domain.Id), CatalogRecords.Encode(domain));
      });
      state = current.With(cluster, domain);
      return domain;
    }
  }

  /// <summary>
  /// Adds a column whose storage does not exist yet. The column has the next column id.
  /// </summary>
  /// <exception cref="SchemaObjectAlreadyExistsException">A column with the name exists.</exception>
  internal ColumnRecord CreateColumn(string name, ColumnKind kind, long domainId,
      ColumnEncoding keySerializer, ColumnEncoding valueSerializer, ColumnEncoding comparer,
      string keyType, string valueType)
  {
    ValidateName(name, "column");
    lock (WriteLock)
    {
      var current = state;
      if (current.FindColumn(name) != null)
        throw new SchemaObjectAlreadyExistsException($"Column '{name}' already exists.");
      if (domainId != 0 && current.FindDomain(domainId) == null)
        throw new KeyNotFoundException($"Domain {domainId} does not exist.");
      var cluster = current.Cluster with
      {
        SchemaEpoch = current.Cluster.SchemaEpoch + 1,
        NextColumnId = current.Cluster.NextColumnId + 1
      };
      var column = new ColumnRecord(current.Cluster.NextColumnId, name, kind, domainId,
          cluster.SchemaEpoch, ColumnState.Creating, keySerializer, valueSerializer, comparer,
          keyType, valueType);
      Commit(Tree, transaction =>
      {
        transaction.Upsert(CatalogRecords.ClusterKey, CatalogRecords.Encode(cluster));
        transaction.Upsert(CatalogRecords.ColumnKey(column.Id), CatalogRecords.Encode(column));
      });
      state = current.With(cluster, column);
      return column;
    }
  }

  /// <summary>Records that a column's storage was created.</summary>
  internal ColumnRecord MarkColumnReady(long id)
  {
    lock (WriteLock)
    {
      var current = state;
      var column = current.FindColumn(id);
      if (column.State == ColumnState.Ready)
        return column;
      var ready = column with { State = ColumnState.Ready };
      Commit(Tree, transaction => transaction.Upsert(CatalogRecords.ColumnKey(id), CatalogRecords.Encode(ready)));
      state = current.With(current.Cluster, ready);
      return ready;
    }
  }

  /// <summary>
  /// Drops a live column: its name becomes free at once, and its record stays, marked dropped,
  /// until <see cref="PurgeColumn"/> after its storage is deleted.
  /// </summary>
  internal void DropColumn(long id)
  {
    lock (WriteLock)
    {
      var current = state;
      var column = current.FindColumn(id);
      if (column == null || column.State == ColumnState.Dropped)
        throw new KeyNotFoundException($"Column {id} does not exist.");
      var cluster = current.Cluster with { SchemaEpoch = current.Cluster.SchemaEpoch + 1 };
      var dropped = column with { State = ColumnState.Dropped, DroppedEpoch = cluster.SchemaEpoch };
      Commit(Tree, transaction =>
      {
        transaction.Upsert(CatalogRecords.ClusterKey, CatalogRecords.Encode(cluster));
        transaction.Upsert(CatalogRecords.ColumnKey(id), CatalogRecords.Encode(dropped));
      });
      state = current.With(cluster, dropped);
    }
  }

  /// <summary>
  /// Removes the record of a dropped column whose storage is deleted. The id stays used:
  /// column ids are never reused.
  /// </summary>
  internal void PurgeColumn(long id)
  {
    lock (WriteLock)
    {
      var current = state;
      if (current.FindColumn(id)?.State != ColumnState.Dropped)
        return;
      Commit(Tree, transaction => transaction.Delete(CatalogRecords.ColumnKey(id)));
      state = current.WithoutColumn(id);
    }
  }

  /// <summary>Renames a live column. Its id, kind, domain and encodings stay.</summary>
  /// <exception cref="SchemaObjectAlreadyExistsException">A column with the new name exists.</exception>
  internal void RenameColumn(long id, string name)
  {
    ValidateName(name, "column");
    lock (WriteLock)
    {
      var current = state;
      var column = current.FindColumn(id);
      if (column == null || column.State == ColumnState.Dropped)
        throw new KeyNotFoundException($"Column {id} does not exist.");
      if (current.FindColumn(name) != null)
        throw new SchemaObjectAlreadyExistsException($"Column '{name}' already exists.");
      var cluster = current.Cluster with { SchemaEpoch = current.Cluster.SchemaEpoch + 1 };
      var renamed = column with { Name = name };
      Commit(Tree, transaction =>
      {
        transaction.Upsert(CatalogRecords.ClusterKey, CatalogRecords.Encode(cluster));
        transaction.Upsert(CatalogRecords.ColumnKey(id), CatalogRecords.Encode(renamed));
      });
      state = current.With(cluster, renamed);
    }
  }

  /// <summary>Drops a domain without live columns. The id stays used: domain ids are never reused.</summary>
  /// <exception cref="SchemaConflictException">The domain has live columns.</exception>
  internal void DropDomain(long id)
  {
    lock (WriteLock)
    {
      var current = state;
      var domain = current.FindDomain(id) ?? throw new KeyNotFoundException($"Domain {id} does not exist.");
      if (current.ColumnNamesOf(id).Count != 0)
        throw new SchemaConflictException($"Domain '{domain.Name}' has columns. Drop them first.");
      var cluster = current.Cluster with { SchemaEpoch = current.Cluster.SchemaEpoch + 1 };
      Commit(Tree, transaction =>
      {
        transaction.Upsert(CatalogRecords.ClusterKey, CatalogRecords.Encode(cluster));
        transaction.Delete(CatalogRecords.DomainKey(id));
      });
      state = current.WithoutDomain(cluster, id);
    }
  }

  /// <summary>Renames a domain. Its id, columns and timestamps stay.</summary>
  /// <exception cref="SchemaObjectAlreadyExistsException">A domain with the new name exists.</exception>
  internal void RenameDomain(long id, string name)
  {
    ValidateName(name, "domain");
    lock (WriteLock)
    {
      var current = state;
      var domain = current.FindDomain(id) ?? throw new KeyNotFoundException($"Domain {id} does not exist.");
      if (current.FindDomain(name) != null)
        throw new SchemaObjectAlreadyExistsException($"Domain '{name}' already exists.");
      var cluster = current.Cluster with { SchemaEpoch = current.Cluster.SchemaEpoch + 1 };
      var renamed = domain with { Name = name };
      Commit(Tree, transaction =>
      {
        transaction.Upsert(CatalogRecords.ClusterKey, CatalogRecords.Encode(cluster));
        transaction.Upsert(CatalogRecords.DomainKey(id), CatalogRecords.Encode(renamed));
      });
      state = current.With(cluster, renamed);
    }
  }

  /// <summary>
  /// Raises the timestamp bounds of domains. A bound never decreases, so a domain never issues
  /// an id or timestamp twice, even after a restart.
  /// </summary>
  internal void ReserveTimestamps(IReadOnlyCollection<(long DomainId, long Bound)> reservations)
  {
    if (reservations.Count == 0)
      return;
    lock (WriteLock)
    {
      var current = state;
      // A domain dropped meanwhile needs no reservation.
      var domains = reservations
          .Select(reservation => (Domain: current.FindDomain(reservation.DomainId), reservation.Bound))
          .Where(reservation => reservation.Domain != null)
          .Select(reservation => reservation.Domain with
          {
            TimestampBound = Math.Max(reservation.Domain.TimestampBound, reservation.Bound)
          })
          .ToArray();
      if (domains.Length == 0)
        return;
      Commit(Tree, transaction =>
      {
        foreach (var domain in domains)
          transaction.Upsert(CatalogRecords.DomainKey(domain.Id), CatalogRecords.Encode(domain));
      });
      state = current.With(domains);
    }
  }

  static void Commit(ITransactionalZoneTree<string, Memory<byte>> tree,
      Action<ITransaction<string, Memory<byte>>> write)
  {
    using var transaction = tree.BeginTransaction();
    write(transaction);
    // Waiting on the commit task needs no thread pool thread: the commit sync thread completes it.
    transaction.CommitDurableAsync().GetAwaiter().GetResult();
  }

  internal static void ValidateName(string name, string kind)
  {
    if (string.IsNullOrWhiteSpace(name))
      throw new ArgumentException($"A {kind} needs a nonempty name.", nameof(name));
    if (name.Length > MaximumNameLength)
      throw new ArgumentException($"A {kind} name has at most {MaximumNameLength} characters.", nameof(name));
    foreach (var character in name)
    {
      if (char.IsControl(character))
        throw new ArgumentException($"A {kind} name cannot contain control characters.", nameof(name));
    }
  }

  /// <summary>Deletes the catalog. The catalog must not be used afterward.</summary>
  internal void Drop() => Tree.Maintenance.Drop();

  public void Dispose() => Tree.Dispose();
}
