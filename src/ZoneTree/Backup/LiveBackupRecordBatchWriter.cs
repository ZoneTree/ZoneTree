using ZoneTree.Compression;
using ZoneTree.Options;

namespace ZoneTree.Backup;

/// <summary>
/// Writes the binary record-batch format used by live backup.
/// </summary>
public sealed class LiveBackupRecordBatchWriter : ILiveBackupRecordWriter
{
  readonly LiveBackupHashStream Destination;

  readonly LiveBackupRecordBatch Batch;

  readonly MemoryStream BlockBuffer;

  public LiveBackupRecordBatchWriter(
      Stream destination,
      LiveBackupRecordBatch batch)
      : this(destination, batch, leaveOpen: false)
  {
  }

  internal LiveBackupRecordBatchWriter(Stream destination, LiveBackupRecordBatch batch, bool leaveOpen)
  {
    Batch = batch ?? throw new ArgumentNullException(nameof(batch));
    Destination = new LiveBackupHashStream(destination ?? throw new ArgumentNullException(nameof(destination)), leaveOpen);
    if (batch.CompressionMethod != CompressionMethod.None)
      BlockBuffer = new MemoryStream(batch.CompressionBlockSize);
  }

  public async Task WriteAsync(
      LiveBackupRecord record,
      CancellationToken cancellationToken)
  {
    var destination = BlockBuffer ?? (Stream)Destination;
    await WriteRecordAsync(
        destination,
        record,
        cancellationToken);

    if (BlockBuffer != null)
    {
      if (BlockBuffer.Length >= Batch.CompressionBlockSize)
        await FlushBlockAsync(cancellationToken);
      return;
    }

    var recordLength =
        sizeof(long) +
        sizeof(byte) +
        sizeof(int) * 2L +
        record.Key.Length +
        record.Value.Length;
    Batch.UncompressedLength += recordLength;
    Batch.StoredLength += recordLength;
  }

  static async Task WriteRecordAsync(
      Stream destination,
      LiveBackupRecord record,
      CancellationToken cancellationToken)
  {
    await WriteInt64Async(destination, record.RecordIndex, cancellationToken);
    var isDeleted = new[] { record.IsDeleted ? (byte)1 : (byte)0 };
    await destination.WriteAsync(isDeleted, cancellationToken);
    await WriteInt32Async(destination, record.Key.Length, cancellationToken);
    await WriteInt32Async(destination, record.Value.Length, cancellationToken);
    await destination.WriteAsync(record.Key, cancellationToken);
    await destination.WriteAsync(record.Value, cancellationToken);
  }

  async Task FlushBlockAsync(CancellationToken cancellationToken)
  {
    if (BlockBuffer == null || BlockBuffer.Length == 0)
      return;

    var uncompressed = BlockBuffer.ToArray();
    var compressed = DataCompression
        .Compress(
            Batch.CompressionMethod,
            Batch.CompressionLevel,
            uncompressed);
    await WriteInt32Async(Destination, uncompressed.Length, cancellationToken);
    await WriteInt32Async(Destination, compressed.Length, cancellationToken);
    await Destination.WriteAsync(compressed, cancellationToken);

    Batch.UncompressedLength += uncompressed.Length;
    Batch.StoredLength += sizeof(int) * 2L + compressed.Length;
    BlockBuffer.SetLength(0);
  }

  static Task WriteInt32Async(
      Stream destination,
      int value,
      CancellationToken cancellationToken)
  {
    var bytes = BitConverter.GetBytes(value);
    return destination.WriteAsync(bytes, cancellationToken).AsTask();
  }

  static Task WriteInt64Async(
      Stream destination,
      long value,
      CancellationToken cancellationToken)
  {
    var bytes = BitConverter.GetBytes(value);
    return destination.WriteAsync(bytes, cancellationToken).AsTask();
  }

  public async ValueTask DisposeAsync()
  {
    try
    {
      await FlushBlockAsync(CancellationToken.None).ConfigureAwait(false);
      await Destination.FlushAsync().ConfigureAwait(false);
      Batch.Sha256 = Destination.GetChecksum();
    }
    finally
    {
      BlockBuffer?.Dispose();
      await Destination.DisposeAsync().ConfigureAwait(false);
    }
  }
}
