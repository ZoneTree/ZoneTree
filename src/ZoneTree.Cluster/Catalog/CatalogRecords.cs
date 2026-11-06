using System.Text;

namespace ZoneTree.Cluster;

/// <summary>The storage state of a column in the catalog.</summary>
internal enum ColumnState : byte
{
  /// <summary>The column is in the catalog, and its storage may not exist yet.</summary>
  Creating = 0,

  /// <summary>The column's storage was created.</summary>
  Ready = 1,

  /// <summary>
  /// The column was dropped. Its name is free, and its storage is deleted before the record is.
  /// </summary>
  Dropped = 2,
}

/// <summary>The catalog record of the cluster itself.</summary>
internal sealed record ClusterRecord(
    Guid ClusterId, long SchemaEpoch, long NextDomainId, long NextColumnId);

/// <summary>
/// A range of commit sequence numbers, from <see cref="From"/> exclusive to <see cref="To"/>
/// inclusive.
/// </summary>
internal readonly record struct SequenceRange(long From, long To)
{
  internal bool Contains(long sequence) => sequence > From && sequence <= To;
}

/// <summary>The catalog record of a domain.</summary>
/// <remarks>
/// A process numbers the domain's commits from the timestamp bound it opened with. A commit
/// takes at least one timestamp, so its sequence number is at or below its timestamp, and
/// every number of a process is below the numbers of the next one.
/// </remarks>
/// <param name="TimestampBound">
/// The highest transaction id and commit timestamp the domain may issue before it reserves more.
/// </param>
/// <param name="DurableSequence">
/// Every commit at or below this sequence number is complete and synced in every column of
/// the domain, or rolled back. Set to the starting point of each process once the commits
/// before it are settled, and when a column of the domain is dropped; the domain's syncs do
/// not write the catalog.
/// </param>
/// <param name="IsClean">True if the domain was closed cleanly after its last open.</param>
/// <param name="RolledBackRanges">
/// The commit sequence numbers that recoveries rolled back: after an unclean shutdown, the
/// commits from the first one the crash left missing or incomplete up to the timestamp bound
/// of the closed process. A column that opens later rolls back its commits in these ranges too.
/// </param>
internal sealed record DomainRecord(long Id, string Name, long TimestampBound,
    long DurableSequence, bool IsClean, IReadOnlyList<SequenceRange> RolledBackRanges)
{
  internal bool IsRolledBack(long sequence)
  {
    foreach (var range in RolledBackRanges)
    {
      if (range.Contains(sequence))
        return true;
    }
    return false;
  }
}

/// <summary>
/// The catalog record of a column. A column outside domains has domain id zero, and a column
/// that was not dropped has dropped epoch zero.
/// </summary>
internal sealed record ColumnRecord(
    long Id, string Name, ColumnKind Kind, long DomainId, long CreatedEpoch, ColumnState State,
    ColumnEncoding KeySerializer, ColumnEncoding ValueSerializer, ColumnEncoding Comparer,
    string KeyType, string ValueType, long DroppedEpoch = 0);

/// <summary>
/// The keys and value formats of the catalog records. Each value starts with a format byte.
/// </summary>
internal static class CatalogRecords
{
  /// <summary>The catalog format. It stays 1 until the cluster is released.</summary>
  internal const int FormatVersion = 1;

  const byte RecordFormat = 1;

  internal const string ClusterKey = "cluster";
  internal const string DomainPrefix = "domain/";
  internal const string ColumnPrefix = "column/";

  // Ids are zero-padded so that the keys of each kind sort in id order.
  internal static string DomainKey(long id) => DomainPrefix + id.ToString("D19", System.Globalization.CultureInfo.InvariantCulture);
  internal static string ColumnKey(long id) => ColumnPrefix + id.ToString("D19", System.Globalization.CultureInfo.InvariantCulture);

  internal static Memory<byte> Encode(ClusterRecord record) => Write(writer =>
  {
    writer.Write(FormatVersion);
    writer.Write(record.ClusterId.ToByteArray());
    writer.Write(record.SchemaEpoch);
    writer.Write(record.NextDomainId);
    writer.Write(record.NextColumnId);
  });

  internal static ClusterRecord DecodeCluster(Memory<byte> value) => Read(value, reader =>
  {
    var format = reader.ReadInt32();
    if (format != FormatVersion)
      throw new InvalidDataException($"The cluster catalog has format {format}, not {FormatVersion}.");
    return new ClusterRecord(new Guid(reader.ReadBytes(16)), reader.ReadInt64(),
        reader.ReadInt64(), reader.ReadInt64());
  });

  internal static Memory<byte> Encode(DomainRecord record) => Write(writer =>
  {
    writer.Write(record.Id);
    writer.Write(record.Name);
    writer.Write(record.TimestampBound);
    writer.Write(record.DurableSequence);
    writer.Write(record.IsClean);
    writer.Write(record.RolledBackRanges.Count);
    foreach (var range in record.RolledBackRanges)
    {
      writer.Write(range.From);
      writer.Write(range.To);
    }
  });

  internal static DomainRecord DecodeDomain(Memory<byte> value) => Read(value, reader =>
  {
    var id = reader.ReadInt64();
    var name = reader.ReadString();
    var bound = reader.ReadInt64();
    var durable = reader.ReadInt64();
    var clean = reader.ReadBoolean();
    var ranges = new SequenceRange[reader.ReadInt32()];
    for (var index = 0; index < ranges.Length; ++index)
      ranges[index] = new SequenceRange(reader.ReadInt64(), reader.ReadInt64());
    return new DomainRecord(id, name, bound, durable, clean, ranges);
  });

  internal static Memory<byte> Encode(ColumnRecord record) => Write(writer =>
  {
    writer.Write(record.Id);
    writer.Write(record.Name);
    writer.Write((byte)record.Kind);
    writer.Write(record.DomainId);
    writer.Write(record.CreatedEpoch);
    writer.Write((byte)record.State);
    WriteEncoding(writer, record.KeySerializer);
    WriteEncoding(writer, record.ValueSerializer);
    WriteEncoding(writer, record.Comparer);
    writer.Write(record.KeyType);
    writer.Write(record.ValueType);
    writer.Write(record.DroppedEpoch);
  });

  internal static ColumnRecord DecodeColumn(Memory<byte> value) => Read(value, reader =>
  {
    var id = reader.ReadInt64();
    var name = reader.ReadString();
    var kind = (ColumnKind)reader.ReadByte();
    if (!Enum.IsDefined(kind))
      throw new InvalidDataException($"Column '{name}' has an unknown kind {kind} in the cluster catalog.");
    var domainId = reader.ReadInt64();
    var createdEpoch = reader.ReadInt64();
    var state = (ColumnState)reader.ReadByte();
    if (!Enum.IsDefined(state))
      throw new InvalidDataException($"Column '{name}' has an unknown state {state} in the cluster catalog.");
    return new ColumnRecord(id, name, kind, domainId, createdEpoch, state,
        ReadEncoding(reader), ReadEncoding(reader), ReadEncoding(reader),
        reader.ReadString(), reader.ReadString(), reader.ReadInt64());
  });

  static void WriteEncoding(BinaryWriter writer, ColumnEncoding encoding)
  {
    writer.Write(encoding.Id);
    writer.Write(encoding.Version);
  }

  static ColumnEncoding ReadEncoding(BinaryReader reader) => new(reader.ReadString(), reader.ReadInt32());

  static Memory<byte> Write(Action<BinaryWriter> write)
  {
    using var stream = new MemoryStream();
    using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
    {
      writer.Write(RecordFormat);
      write(writer);
    }
    return stream.ToArray();
  }

  static T Read<T>(Memory<byte> value, Func<BinaryReader, T> read)
  {
    using var stream = new MemoryStream(value.ToArray(), writable: false);
    using var reader = new BinaryReader(stream, Encoding.UTF8);
    var format = reader.ReadByte();
    if (format != RecordFormat)
      throw new InvalidDataException($"A cluster catalog record has format {format}, not {RecordFormat}.");
    var record = read(reader);
    if (stream.Position != stream.Length)
      throw new InvalidDataException("A cluster catalog record has unexpected trailing data.");
    return record;
  }
}
