using ZoneTree.Compression;
using ZoneTree.Options;

namespace ZoneTree.Backup;

/// <summary>
/// Reads the binary record-batch format used by live backup.
/// </summary>
/// <remarks>Consume through the end of the batch to validate its checksum, lengths and record count.</remarks>
public sealed class LiveBackupRecordBatchReader : IDisposable
{
  readonly LiveBackupHashStream Source;

  readonly LiveBackupRecordBatch Batch;

  MemoryStream BlockBuffer;
  long RecordsRead;
  long UncompressedBytesRead;
  bool Validated;

  public LiveBackupRecordBatchReader(
      Stream source,
      LiveBackupRecordBatch batch)
      : this(source, batch, leaveOpen: false)
  {
  }

  internal LiveBackupRecordBatchReader(Stream source, LiveBackupRecordBatch batch, bool leaveOpen)
  {
    Batch = batch ?? throw new ArgumentNullException(nameof(batch));
    ArgumentNullException.ThrowIfNull(source);
    if (batch.RecordCount < 0 || batch.StoredLength < 0 || batch.UncompressedLength < 0)
      throw new LiveBackupIntegrityException($"record batch {batch.BatchId}", "Negative count or length.");
    Source = new LiveBackupHashStream(source, leaveOpen);
  }

  public bool TryRead(out LiveBackupRecord record)
  {
    record = null;
    if (Validated) return false;
    var found = TryReadNext(out record);
    if (!found)
    {
      Validate();
      Validated = true;
      return false;
    }
    ++RecordsRead;
    if (RecordsRead > Batch.RecordCount || record.RecordIndex != RecordsRead)
      throw new LiveBackupIntegrityException($"record batch {Batch.BatchId}", "Unexpected record count or index.");
    UncompressedBytesRead = checked(UncompressedBytesRead + sizeof(long) + sizeof(byte) + 2L * sizeof(int) + record.Key.Length + record.Value.Length);
    return true;
  }

  void Validate()
  {
    var name = $"record batch {Batch.BatchId}";
    if (RecordsRead != Batch.RecordCount || UncompressedBytesRead != Batch.UncompressedLength)
      throw new LiveBackupIntegrityException(name,
          $"Expected {Batch.RecordCount} records / {Batch.UncompressedLength} uncompressed bytes, read {RecordsRead} / {UncompressedBytesRead}.");
    Source.Validate(Batch.StoredLength, Batch.Sha256, name);
  }

  bool TryReadNext(out LiveBackupRecord record)
  {
    if (Batch.CompressionMethod == CompressionMethod.None)
      return TryReadRecord(Source, Batch.UncompressedLength - UncompressedBytesRead, out record);

    while (BlockBuffer == null || BlockBuffer.Position == BlockBuffer.Length)
    {
      BlockBuffer?.Dispose();
      BlockBuffer = null;
      if (!TryReadCompressedBlock(out var block))
      {
        record = null;
        return false;
      }
      BlockBuffer = new MemoryStream(block, writable: false);
    }
    return TryReadRecord(BlockBuffer, Math.Min(BlockBuffer.Length - BlockBuffer.Position,
        Batch.UncompressedLength - UncompressedBytesRead), out record);
  }

  bool TryReadCompressedBlock(out byte[] block)
  {
    block = null;
    if (!TryReadInt32(Source, out var uncompressedLength))
      return false;
    if (!TryReadInt32(Source, out var compressedLength))
      throw new EndOfStreamException();
    if (uncompressedLength <= 0 || uncompressedLength > Batch.UncompressedLength - UncompressedBytesRead ||
        compressedLength <= 0 || compressedLength > Batch.StoredLength - Source.BytesTransferred)
      throw new LiveBackupIntegrityException($"record batch {Batch.BatchId}", "Invalid compressed block lengths.");
    var compressed = ReadBytes(Source, compressedLength);
    Memory<byte> decompressed;
    try { decompressed = DataCompression.Decompress(Batch.CompressionMethod, compressed, uncompressedLength); }
    catch (Exception error) when (error is ZstdSharp.ZstdException or InvalidDataException or ArgumentException)
    { throw new LiveBackupIntegrityException($"record batch {Batch.BatchId}", "Compressed block is damaged or invalid.", error); }
    if (decompressed.Length != uncompressedLength)
      throw new InvalidDataException("Invalid live backup compressed block length.");
    block = decompressed.ToArray();
    return true;
  }

  static bool TryReadRecord(
      Stream source,
      long remainingLength,
      out LiveBackupRecord record)
  {
    record = null;
    if (!TryReadInt64(source, out var recordIndex))
      return false;
    var isDeleted = source.ReadByte();
    if (isDeleted < 0)
      throw new EndOfStreamException();
    if (isDeleted > 1)
      throw new InvalidDataException("Invalid live backup deletion flag.");
    if (!TryReadInt32(source, out var keyLength))
      throw new EndOfStreamException();
    if (!TryReadInt32(source, out var valueLength))
      throw new EndOfStreamException();
    if (keyLength < 0 || valueLength < 0 ||
        sizeof(long) + sizeof(byte) + 2L * sizeof(int) + keyLength + valueLength > remainingLength)
      throw new InvalidDataException("Invalid live backup record length.");
    record = new LiveBackupRecord
    {
      RecordIndex = recordIndex,
      IsDeleted = isDeleted != 0,
      Key = ReadBytes(source, keyLength),
      Value = ReadBytes(source, valueLength)
    };
    return true;
  }

  static bool TryReadInt32(Stream source, out int value)
  {
    Span<byte> bytes = stackalloc byte[sizeof(int)];
    if (!TryReadExactly(source, bytes))
    {
      value = 0;
      return false;
    }
    value = BitConverter.ToInt32(bytes);
    return true;
  }

  static bool TryReadInt64(Stream source, out long value)
  {
    Span<byte> bytes = stackalloc byte[sizeof(long)];
    if (!TryReadExactly(source, bytes))
    {
      value = 0;
      return false;
    }
    value = BitConverter.ToInt64(bytes);
    return true;
  }

  static bool TryReadExactly(
      Stream source,
      Span<byte> bytes)
  {
    var read = source.Read(bytes);
    if (read == 0)
      return false;
    while (read < bytes.Length)
    {
      var count = source.Read(bytes[read..]);
      if (count == 0)
        throw new EndOfStreamException();
      read += count;
    }
    return true;
  }

  static byte[] ReadBytes(
      Stream source,
      int length)
  {
    if (length < 0)
      throw new InvalidDataException("Invalid live backup record length.");
    var bytes = new byte[length];
    var read = 0;
    while (read < length)
    {
      var count = source.Read(bytes, read, length - read);
      if (count == 0)
        throw new EndOfStreamException();
      read += count;
    }
    return bytes;
  }

  public void Dispose()
  {
    BlockBuffer?.Dispose();
    Source.Dispose();
  }
}
