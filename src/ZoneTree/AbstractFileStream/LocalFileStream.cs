namespace ZoneTree.AbstractFileStream;

#pragma warning disable CA2215

public sealed class LocalFileStream : Stream, IFileStream
{
  readonly FileStream FileStream;

  public LocalFileStream(string path,
      FileMode mode,
      FileAccess access,
      FileShare share,
      int bufferSize,
      FileOptions options)
  {
    FilePath = path;
    FileStream = new FileStream(path, mode, access, share, bufferSize, options);
  }

  public string FilePath { get; }

  internal FileStream BaseFileStream => FileStream;

  public override bool CanRead => FileStream.CanRead;

  public override bool CanSeek => FileStream.CanSeek;

  public override bool CanWrite => FileStream.CanWrite;

  public override long Length => FileStream.Length;

  public override long Position
  {
    get => FileStream.Position;
    set => FileStream.Position = value;
  }

  public override void Flush()
  {
    FileStream.Flush();
  }

  public void Flush(bool flushToDisk)
  {
    FileStream.Flush(flushToDisk);
  }

  public override int Read(byte[] buffer, int offset, int count)
  {
    return FileStream.Read(buffer, offset, count);
  }

  public int ReadFaster(byte[] buffer, int offset, int count)
  {
    int totalRead = 0;
    while (totalRead < count)
    {
      int read = FileStream.Read(buffer, offset + totalRead, count - totalRead);
      if (read == 0)
      {
        throw new EndOfStreamException();
      }
      totalRead += read;
    }
    return totalRead;
  }

  public override long Seek(long offset, SeekOrigin origin)
  {
    return FileStream.Seek(offset, origin);
  }

  public override void SetLength(long value)
  {
    FileStream.SetLength(value);
  }

  public Stream ToStream()
  {
    return this;
  }

  public override void Write(byte[] buffer, int offset, int count)
  {
    FileStream.Write(buffer, offset, count);
  }

  public override void Close()
  {
    FileStream.Close();
  }

  public new void Dispose()
  {
    FileStream.Dispose();
    GC.SuppressFinalize(this);
  }

  public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
  {
    return FileStream.ReadAsync(buffer, offset, count, cancellationToken);
  }

  public override int Read(Span<byte> buffer)
  {
    return FileStream.Read(buffer);
  }

  public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
  {
    return FileStream.ReadAsync(buffer, cancellationToken);
  }

  public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
  {
    return FileStream.BeginRead(buffer, offset, count, callback, state);
  }

  public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
  {
    return FileStream.BeginWrite(buffer, offset, count, callback, state);
  }

  public override bool CanTimeout => FileStream.CanTimeout;

  public override void CopyTo(Stream destination, int bufferSize)
  {
    FileStream.CopyTo(destination, bufferSize);
  }

  public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
  {
    return FileStream.CopyToAsync(destination, bufferSize, cancellationToken);
  }

  public override int EndRead(IAsyncResult asyncResult)
  {
    return FileStream.EndRead(asyncResult);
  }

  public override void EndWrite(IAsyncResult asyncResult)
  {
    FileStream.EndWrite(asyncResult);
  }

  public override ValueTask DisposeAsync()
  {
    return FileStream.DisposeAsync();
  }

  public override Task FlushAsync(CancellationToken cancellationToken)
  {
    return FileStream.FlushAsync(cancellationToken);
  }

  public override int ReadByte()
  {
    return FileStream.ReadByte();
  }

  public override void Write(ReadOnlySpan<byte> buffer)
  {
    FileStream.Write(buffer);
  }

  public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
  {
    return FileStream.WriteAsync(buffer, offset, count, cancellationToken);
  }

  public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
  {
    return FileStream.WriteAsync(buffer, cancellationToken);
  }

  public override void WriteByte(byte value)
  {
    FileStream.WriteByte(value);
  }

  public override int ReadTimeout
  {
    get => FileStream.ReadTimeout;
    set => FileStream.ReadTimeout = value;
  }

  public override int WriteTimeout
  {
    get => FileStream.WriteTimeout;
    set => FileStream.WriteTimeout = value;
  }

  protected override void Dispose(bool disposing)
  {
    FileStream.Dispose();
  }
}

#pragma warning restore CA2215
