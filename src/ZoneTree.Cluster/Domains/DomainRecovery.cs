using System.Buffers.Binary;
using ZoneTree.Transactional.Versioning;
using ZoneTree.WAL;

namespace ZoneTree.Cluster;

/// <summary>
/// Finds which commits of a domain survived an unclean shutdown, from the columns themselves,
/// before any of them opens.
/// </summary>
/// <remarks>
/// <para>
/// The domain numbers its commits without gaps. A commit is complete when every column it
/// wrote holds its commit record, each of which holds the commit's number and the number of
/// those columns. The survivors are the commits before the first number recovery cannot find
/// complete, which is a prefix of the commit order, as one tree keeps. A commit that reached
/// none of its columns leaves its number missing, so no survivor follows a lost commit.
/// </para>
/// <para>
/// After a process crash with the shared journal, no caller was told of a rolled back commit:
/// a commit becomes visible only after every earlier one wrote its records, and a commit that
/// failed after it took its number stops the domain's commits. A durable commit is never rolled
/// back: it returns only after a sync that covered every earlier commit.
/// </para>
/// <para>
/// Recovery reads the commit records in the coordinated columns' journals, without their types.
/// A merge moves commit records to a disk segment only after it made them durable and wrote
/// the column's durable watermark, so every commit at or below the highest watermark is
/// complete, and so is every commit at or below the durable sequence number in the catalog.
/// Reading a journal syncs it, so the survivors are durable once recovery is done.
/// </para>
/// </remarks>
internal static class DomainRecovery
{
  /// <summary>
  /// Returns the cut: every commit at or below this sequence number survived, and every one
  /// above it up to the clock start is rolled back.
  /// </summary>
  internal static long FindCut(ZoneTreeCluster cluster, DomainRecord domain, long clockStart)
  {
    var floor = domain.DurableSequence;
    // The commits by sequence number: their participants, and the columns that hold them.
    var commits = new Dictionary<long, (int Participants, int Found)>();
    // A dropped column whose storage still exists holds commit records of its transactions.
    var columns = cluster.Catalog.State.Columns.Concat(cluster.Catalog.State.DroppedColumns)
        .Where(column => column.Kind == ColumnKind.Coordinated && column.DomainId == domain.Id);
    foreach (var column in columns)
    {
      var records = SharedJournalReader.ReadLiveRecords(cluster.Logger, cluster.FileStreamProvider,
          cluster.ColumnDirectory(column.Id), cluster.CoordinatedWriteAheadLog);
      var (statuses, watermark) = ReadStatuses(records);
      floor = Math.Max(floor, watermark);
      foreach (var meta in statuses)
      {
        if (meta == CommitRecord.Pending)
          continue;
        var sequence = CommitRecord.Sequence(meta);
        commits[sequence] = commits.TryGetValue(sequence, out var known)
            ? (known.Participants, known.Found + 1)
            : (CommitRecord.Participants(meta), 1);
      }
    }
    // The numbers at or below the floor were settled before; the ranges earlier recoveries
    // rolled back are among them.
    var cut = floor;
    while (cut < clockStart &&
        commits.TryGetValue(cut + 1, out var commit) && commit.Found == commit.Participants)
      ++cut;
    return cut;
  }

  /// <summary>
  /// The final status records of a column, as its own recovery replays the journal, and its
  /// highest durable watermark.
  /// </summary>
  static (IEnumerable<long> Statuses, long Watermark) ReadStatuses(
      List<SharedJournalReader.Record> records)
  {
    var statuses = new Dictionary<long, long>();
    long watermark = 0;
    foreach (var (key, value) in records)
    {
      var keyBytes = key.Span;
      var kind = (VersionedKeyKind)keyBytes[0];
      if (kind == VersionedKeyKind.Data)
        continue;
      var version = BinaryPrimitives.ReadInt64LittleEndian(keyBytes[1..]);
      var valueBytes = value.Span;
      var valueKind = (VersionedValueKind)valueBytes[0];
      var meta = valueKind == VersionedValueKind.System
          ? BinaryPrimitives.ReadInt64LittleEndian(valueBytes[1..])
          : 0;
      if (kind == VersionedKeyKind.Clock)
      {
        if (version == CommitRecord.DurableWatermarkVersion && valueKind == VersionedValueKind.System)
          watermark = Math.Max(watermark, meta);
        continue;
      }
      // A later record of the same transaction replaces the earlier one; a rollback deletes it.
      if (valueKind == VersionedValueKind.System)
        statuses[version] = meta;
      else
        statuses.Remove(version);
    }
    return (statuses.Values, watermark);
  }
}
