using System.Security.Cryptography;

namespace ZoneTree.Cluster;

/// <summary>The cut of a domain in a cluster backup generation.</summary>
/// <param name="DomainId">The domain.</param>
/// <param name="Sequence">
/// The sequence number of the newest commit the cut includes. Every column of the domain holds
/// every commit at or below it; restore rolls back every later one.
/// </param>
/// <param name="TimestampBound">
/// The domain's timestamp bound after the columns were captured, at or above every timestamp,
/// and so every sequence number, that the captured columns hold.
/// </param>
internal readonly record struct ClusterBackupCut(long DomainId, long Sequence, long TimestampBound);

/// <summary>The generation of a column's own store that a cluster backup generation uses.</summary>
internal readonly record struct ClusterBackupColumn(long ColumnId, long GenerationId);

/// <summary>
/// A cluster backup generation: the catalog, each domain's cut, and the generation of each
/// column's store. It is written after every column's generation is published.
/// </summary>
internal sealed record ClusterBackupManifest(
    long GenerationId,
    long CreatedAtUnixSeconds,
    ClusterRecord Cluster,
    IReadOnlyList<DomainRecord> Domains,
    IReadOnlyList<ColumnRecord> Columns,
    IReadOnlyList<ClusterBackupCut> Cuts,
    IReadOnlyList<ClusterBackupColumn> ColumnGenerations)
{
  const int Magic = 0x4243545A; // "ZTCB"
  const int FormatVersion = 1;
  const int HashLength = 32;

  internal byte[] Encode()
  {
    using var stream = new MemoryStream();
    using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
    {
      writer.Write(Magic);
      writer.Write(FormatVersion);
      writer.Write(GenerationId);
      writer.Write(CreatedAtUnixSeconds);
      WriteRecord(writer, CatalogRecords.Encode(Cluster));
      writer.Write(Domains.Count);
      foreach (var domain in Domains)
        WriteRecord(writer, CatalogRecords.Encode(domain));
      writer.Write(Columns.Count);
      foreach (var column in Columns)
        WriteRecord(writer, CatalogRecords.Encode(column));
      writer.Write(Cuts.Count);
      foreach (var cut in Cuts)
      {
        writer.Write(cut.DomainId);
        writer.Write(cut.Sequence);
        writer.Write(cut.TimestampBound);
      }
      writer.Write(ColumnGenerations.Count);
      foreach (var column in ColumnGenerations)
      {
        writer.Write(column.ColumnId);
        writer.Write(column.GenerationId);
      }
    }
    var content = stream.ToArray();
    var result = new byte[content.Length + HashLength];
    content.CopyTo(result, 0);
    SHA256.HashData(content).CopyTo(result, content.Length);
    return result;
  }

  internal static ClusterBackupManifest Decode(byte[] bytes)
  {
    if (bytes.Length < HashLength + 8)
      throw new InvalidDataException("The cluster backup manifest is truncated.");
    var content = bytes.AsSpan(0, bytes.Length - HashLength);
    if (!SHA256.HashData(content).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - HashLength)))
      throw new InvalidDataException("The cluster backup manifest is damaged.");
    using var reader = new BinaryReader(new MemoryStream(bytes, 0, content.Length));
    if (reader.ReadInt32() != Magic)
      throw new InvalidDataException("The file is not a cluster backup manifest.");
    var version = reader.ReadInt32();
    if (version != FormatVersion)
      throw new InvalidDataException($"The cluster backup manifest has format {version}, not {FormatVersion}.");
    var generationId = reader.ReadInt64();
    var createdAt = reader.ReadInt64();
    var cluster = CatalogRecords.DecodeCluster(ReadRecord(reader));
    var domains = new DomainRecord[reader.ReadInt32()];
    for (var index = 0; index < domains.Length; ++index)
      domains[index] = CatalogRecords.DecodeDomain(ReadRecord(reader));
    var columns = new ColumnRecord[reader.ReadInt32()];
    for (var index = 0; index < columns.Length; ++index)
      columns[index] = CatalogRecords.DecodeColumn(ReadRecord(reader));
    var cuts = new ClusterBackupCut[reader.ReadInt32()];
    for (var index = 0; index < cuts.Length; ++index)
      cuts[index] = new ClusterBackupCut(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
    var generations = new ClusterBackupColumn[reader.ReadInt32()];
    for (var index = 0; index < generations.Length; ++index)
      generations[index] = new ClusterBackupColumn(reader.ReadInt64(), reader.ReadInt64());
    return new ClusterBackupManifest(generationId, createdAt, cluster, domains, columns, cuts, generations);
  }

  static void WriteRecord(BinaryWriter writer, Memory<byte> record)
  {
    writer.Write(record.Length);
    writer.Write(record.Span);
  }

  static Memory<byte> ReadRecord(BinaryReader reader) => reader.ReadBytes(reader.ReadInt32());
}
