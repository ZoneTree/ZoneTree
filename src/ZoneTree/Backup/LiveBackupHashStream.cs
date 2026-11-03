using System.Security.Cryptography;

namespace ZoneTree.Backup;

// Hash bytes as they are copied, so creating/restoring a backup needs no second
// file scan and works with non-seekable backup sources as well.
internal sealed class LiveBackupHashStream(Stream inner, bool leaveOpen = false) : Stream
{
  readonly IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
  string FinishedHash;
  bool Disposed;

  public long BytesTransferred { get; private set; }
  public override bool CanRead => inner.CanRead;
  public override bool CanWrite => inner.CanWrite;
  public override bool CanSeek => false;
  public override long Length => throw new NotSupportedException();
  public override long Position { get => BytesTransferred; set => throw new NotSupportedException(); }

  void Append(ReadOnlySpan<byte> bytes)
  {
    if (FinishedHash != null) throw new InvalidOperationException("Backup checksum was already finalized.");
    Hash.AppendData(bytes);
    BytesTransferred = checked(BytesTransferred + bytes.Length);
  }

  public string GetChecksum() => FinishedHash ??= Convert.ToHexString(Hash.GetHashAndReset());

  public void Validate(long length, string checksum, string file)
  {
    if (length < 0 || BytesTransferred != length)
      throw new LiveBackupIntegrityException(file, $"Expected {length} bytes, read {BytesTransferred}.");
    if (checksum != null && !string.Equals(GetChecksum(), checksum, StringComparison.OrdinalIgnoreCase))
      throw new LiveBackupIntegrityException(file, "SHA-256 checksum mismatch.");
  }

  public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
  public override int Read(Span<byte> buffer)
  {
    var count = inner.Read(buffer);
    Append(buffer[..count]);
    return count;
  }
  public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
  {
    var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    Append(buffer.Span[..count]);
    return count;
  }
  public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
      => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

  public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
  public override void Write(ReadOnlySpan<byte> buffer)
  {
    inner.Write(buffer);
    Append(buffer);
  }
  public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
  {
    await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    Append(buffer.Span);
  }
  public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
      => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
  public override void Flush() => inner.Flush();
  public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  protected override void Dispose(bool disposing)
  {
    if (disposing && !Disposed)
    {
      Disposed = true;
      Hash.Dispose();
      if (!leaveOpen) inner.Dispose();
    }
    base.Dispose(disposing);
  }
  public override async ValueTask DisposeAsync()
  {
    try
    {
      if (!Disposed)
      {
        Disposed = true;
        Hash.Dispose();
        if (!leaveOpen) await inner.DisposeAsync().ConfigureAwait(false);
      }
    }
    finally { await base.DisposeAsync().ConfigureAwait(false); }
  }
}
