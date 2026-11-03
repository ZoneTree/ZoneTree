using ZoneTree.Options;

namespace ZoneTree.Backup;

/// <summary>The storage layout required to restore a generation.</summary>
public enum LiveBackupTreeKind { Plain, Transactional }

public sealed class LiveBackupGeneration : IDisposable
{
  public LiveBackupGeneration() { }

  /// <summary>Creates a generation whose retention lease is released by Dispose.</summary>
  public LiveBackupGeneration(IDisposable lease) { Lease = lease; }

  // Local sources keep the immutable catalog open while its files are read.
  // Retention needs exclusive access before retiring that catalog.
  internal IDisposable Lease { get; set; }

  public LiveBackupTreeKind TreeKind { get; set; }

  public long GenerationId { get; set; }

  public long LastOpIndex { get; set; }

  public long[] SegmentIds { get; set; } = [];

  public LiveBackupFile[] Files { get; set; } = [];

  public LiveBackupRecordBatch RecordBatch { get; set; }

  /// <summary>Releases this generation's retention lease. Restore APIs do this automatically.</summary>
  public void Dispose() { Lease?.Dispose(); Lease = null; }
}

public sealed class LiveBackupFile
{
  public long SegmentId { get; set; }

  public int Order { get; set; }

  public string FileName { get; set; }

  public long RecordCount { get; set; }

  public long ByteLength { get; set; }

  /// <summary>SHA-256 of the stored file; null for older backups without checksums.</summary>
  public string Sha256 { get; set; }
}

public sealed class LiveBackupRecord
{
  /// <summary>One-based, consecutive record index within the batch.</summary>
  public long RecordIndex { get; set; }

  public bool IsDeleted { get; set; }

  public ReadOnlyMemory<byte> Key { get; set; }

  public ReadOnlyMemory<byte> Value { get; set; }
}

public sealed class LiveBackupRecordBatch
{
  public long BatchId { get; set; }

  public long RecordCount { get; set; }

  public CompressionMethod CompressionMethod { get; set; } = CompressionMethod.None;

  public int CompressionLevel { get; set; }

  public int CompressionBlockSize { get; set; }

  public long UncompressedLength { get; set; }

  public long StoredLength { get; set; }

  /// <summary>SHA-256 of the stored batch; null for older backups without checksums.</summary>
  public string Sha256 { get; set; }
}
