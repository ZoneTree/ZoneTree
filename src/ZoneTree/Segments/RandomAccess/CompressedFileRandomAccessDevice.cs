using System.Buffers;
using System.Text;
using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Segments.Block;

namespace ZoneTree.Segments.RandomAccess;

#pragma warning disable CA2213

public sealed class CompressedFileRandomAccessDevice : IRandomAccessDevice
{
  const int MetaDataSize = 5;

  readonly Lock SyncRoot = new();

  readonly IFileStreamProvider FileStreamProvider;

  readonly int BlockSize;

  readonly CompressionMethod CompressionMethod;

  readonly int CompressionLevel;

  readonly string Category;

  IFileStream FileStream;

  readonly BinaryReader BinaryReader;

  readonly BinaryWriter BinaryWriter;

  readonly IRandomAccessDeviceManager RandomDeviceManager;

  readonly BlockCache BlockCache;

  readonly List<long> CompressedBlockPositions = new();

  readonly List<int> CompressedBlockLengths = new();

  readonly List<int> DecompressedBlockLengths = new();

  readonly object[] BlockReadLocks = new object[33];

  int NextBlockIndex;

  DecompressedBlock NextBlock;

  int LastBlockLength;

  public string FilePath { get; }

  public long SegmentId { get; }

  public bool Writable { get; }

  public long Length => GetLength();

  public int ReadBufferCount => BlockCache.Count;

  public struct CompressedFileMeta : IEquatable<CompressedFileMeta>
  {
    public int BlockSize;

    public CompressionMethod CompressionMethod;

    public CompressedFileMeta(int blockSize,
        CompressionMethod compressionMethod) : this()
    {
      BlockSize = blockSize;
      CompressionMethod = compressionMethod;
    }

    public override bool Equals(object obj)
    {
      return obj is CompressedFileMeta meta && Equals(meta);
    }

    public bool Equals(CompressedFileMeta other)
    {
      return BlockSize == other.BlockSize &&
             CompressionMethod == other.CompressionMethod;
    }

    public override int GetHashCode()
    {
      return HashCode.Combine(BlockSize, CompressionMethod);
    }

    public static bool operator ==(CompressedFileMeta left, CompressedFileMeta right)
    {
      return left.Equals(right);
    }

    public static bool operator !=(CompressedFileMeta left, CompressedFileMeta right)
    {
      return !(left == right);
    }
  }

  public CompressedFileRandomAccessDevice(
      ILogger logger,
      IFileStreamProvider fileStreamProvider,
      long segmentId,
      string category,
      IRandomAccessDeviceManager randomDeviceManager,
      string filePath,
      bool writable,
      int compressionBlockSize,
      CompressionMethod compressionMethod,
      int compressionLevel,
      int fileIOBufferSize = 4096)
  {
    for (var i = 0; i < BlockReadLocks.Length; ++i)
    {
      BlockReadLocks[i] = new object();
    }

    BlockCache = new();
    FileStreamProvider = fileStreamProvider;
    SegmentId = segmentId;
    Category = category;
    RandomDeviceManager = randomDeviceManager;
    FilePath = filePath;
    Writable = writable;
    BlockSize = compressionBlockSize;
    CompressionMethod = compressionMethod;
    CompressionLevel = compressionLevel;
    var fileMode = writable ? FileMode.OpenOrCreate : FileMode.Open;
    var fileAccess = writable ? FileAccess.ReadWrite : FileAccess.Read;
    var fileShare = FileShare.Read;
    FileStream = fileStreamProvider.CreateFileStream(filePath,
        fileMode,
        fileAccess,
        fileShare, fileIOBufferSize, FileOptions.None);
    BinaryReader = new BinaryReader(FileStream.ToStream(), Encoding.UTF8, true);
    if (writable)
      BinaryWriter = new BinaryWriter(FileStream.ToStream(), Encoding.UTF8, true);
    if (FileStream.Length > 0)
    {
      var meta = ReadMetaData();
      BlockSize = meta.BlockSize;
      CompressionMethod = meta.CompressionMethod;

      (CompressedBlockPositions,
       CompressedBlockLengths,
       DecompressedBlockLengths) =
          ReadCompressedBlockPositionsAndLengths();
      NextBlockIndex = CompressedBlockPositions.Count - 1;
      if (NextBlockIndex == -1)
        NextBlockIndex = 0;
      else
      {
        LastBlockLength = DecompressedBlockLengths.LastOrDefault();
      }
    }
    else
    {
      WriteMetaData();
    }
  }

  CompressedFileMeta ReadMetaData()
  {
    FileStream.Position = 0;
    return new CompressedFileMeta(
        BinaryReader.ReadInt32(),
        (CompressionMethod)BinaryReader.ReadByte());
  }

  void WriteMetaData()
  {
    FileStream.Position = 0;
    BinaryWriter.Write(BlockSize);
    BinaryWriter.Write((byte)CompressionMethod);
    BinaryWriter.Flush();
    return;
  }

  long GetLength()
  {
    var length = NextBlockIndex * (long)BlockSize + LastBlockLength;
    return length;
  }

  public long AppendBytesReturnPosition(Memory<byte> bytes)
  {
    var pos = GetLength();
    var len = bytes.Length;
    var copyLen = 0;
    while (copyLen < len)
    {
      copyLen += AppendBytesInternal(bytes.Span.Slice(copyLen));
    }
    return pos;
  }

  bool TryGetBlockCache(int blockIndex, out DecompressedBlock block)
  {
    return BlockCache.TryGetBlock(blockIndex, out block);
  }

  /// <summary>
  /// Appends the bytes into the block and returns the appended length.
  /// Appends the bytes that fits into the block.
  /// </summary>
  /// <param name="bytes">Bytes</param>
  /// <returns>Appended bytes length.</returns>
  int AppendBytesInternal(ReadOnlySpan<byte> bytes)
  {
    if (NextBlock == null)
    {
      NextBlock = new DecompressedBlock(
          NextBlockIndex, BlockSize, CompressionMethod, CompressionLevel);
    }
    var nextBlock = NextBlock;
    nextBlock.LastAccessTicks = Environment.TickCount64;

    var copyLength = nextBlock.Append(bytes);
    LastBlockLength = nextBlock.Length;
    if (nextBlock.IsFull)
    {
      AppendBlock(nextBlock);
    }
    return copyLength;
  }

  void AppendBlock(DecompressedBlock nextBlock)
  {
    var compressedBytes = nextBlock.Compress();
    var offset = CompressedBlockPositions.LastOrDefault(MetaDataSize);
    offset += CompressedBlockLengths.LastOrDefault();
    FileStream.Position = offset;
    CompressedBlockPositions.Add(offset);
    CompressedBlockLengths.Add(compressedBytes.Length);
    DecompressedBlockLengths.Add(nextBlock.Length);
    FileStream.Write(compressedBytes.Span);
    // Avoid forcing every compression block to stable storage. A compressed
    // device is not valid or publishable until SealDevice writes its terminal
    // block metadata and durably flushes the complete file.
    FileStream.Flush(false);
    BlockCache.RemoveBlock(nextBlock.BlockIndex);
    ++NextBlockIndex;
    LastBlockLength = 0;
    NextBlock = null;
  }

  static int NextDeviceId = 1;
  int DeviceId = Interlocked.Increment(ref NextDeviceId);

  public Memory<byte> GetBytes(long offset, int length, SingleBlockPin blockPin = null)
  {
    var blockIndex = (int)(offset / BlockSize);
    var offsetInBlock = (int)(offset % BlockSize);
    if (offsetInBlock + length > BlockSize)
    {
      var chunk1Len = BlockSize - offsetInBlock;
      var chunk2Len = length - chunk1Len;
      var chunk1 = GetBytes(offset, chunk1Len, blockPin);
      if (chunk1.Length < chunk1Len)
        return chunk1;
      var chunk2 = GetBytes(offset + chunk1Len, chunk2Len, blockPin);
      var bytes = new Memory<byte>(new byte[chunk1.Length + chunk2.Length]);
      chunk1.CopyTo(bytes);
      chunk2.CopyTo(bytes.Slice(chunk1.Length));
      return bytes;
    }
    var block = NextBlock;
    if (block == null || (block.BlockIndex != blockIndex))
    {
      block = blockPin?.Device;
      if (block != null && block.DeviceId != DeviceId) block = null;
    }

    if ((block == null || block.BlockIndex != blockIndex) &&
        !TryGetBlockCache(blockIndex, out block))
    {
      if (blockIndex >= CompressedBlockPositions.Count)
        return Array.Empty<byte>();

      return ReadBlockAndAddToCache(blockIndex, offsetInBlock, length, blockPin);
    }
    block.LastAccessTicks = Environment.TickCount64;
    return block.GetBytes(offsetInBlock, length);
  }

  Memory<byte> ReadBlockAndAddToCache(
      int blockIndex,
      int offsetInBlock,
      int length,
      SingleBlockPin blockPin)
  {
    var blockLock = BlockReadLocks[blockIndex % BlockReadLocks.Length];
    lock (blockLock)
    {
      var block = NextBlock;
      if (block == null || (block.BlockIndex != blockIndex))
      {
        block = blockPin?.Device;
        if (block != null && block.DeviceId != DeviceId) block = null;
      }
      if ((block == null || block.BlockIndex != blockIndex) &&
          !TryGetBlockCache(blockIndex, out block))
      {
        var decompressedBlock = ReadBlock(blockIndex);
        decompressedBlock.DeviceId = DeviceId;
        if (blockPin != null)
        {
          blockPin.Device = decompressedBlock;
          if (blockPin.ContributeToTheBlockCache)
            BlockCache.AddBlock(decompressedBlock);
        }
        else
        {
          BlockCache.AddBlock(decompressedBlock);
        }
        NextBlock = decompressedBlock;
        return decompressedBlock.GetBytes(offsetInBlock, length);
      }
      block.LastAccessTicks = Environment.TickCount64;
      return block.GetBytes(offsetInBlock, length);
    }
  }

  DecompressedBlock ReadBlock(int blockIndex)
  {
    var blockPositionInFile = CompressedBlockPositions[blockIndex];
    var compressedLength = CompressedBlockLengths[blockIndex];
    var pool = ArrayPool<byte>.Shared;
    var compressedBytes = pool.Rent(compressedLength);
    try
    {
      lock (SyncRoot)
      {
        FileStream.Position = blockPositionInFile;
        FileStream.ReadFaster(compressedBytes, 0, compressedLength);
      }
      var decompressedLength = DecompressedBlockLengths[blockIndex];
      var decompressedBlock = DecompressedBlock
          .FromCompressed(
              blockIndex, new Memory<byte>(compressedBytes).Slice(0, compressedLength),
              CompressionMethod, CompressionLevel, decompressedLength);
      decompressedBlock.LastAccessTicks = Environment.TickCount64;
      return decompressedBlock;
    }
    finally
    {
      pool.Return(compressedBytes);
    }
  }

  public void Dispose()
  {
    if (FileStream == null)
      return;
    Close();
  }

  public void Close()
  {
    BlockCache.Clear();
    if (FileStream == null)
      return;
    FileStream.Flush(true);
    FileStream.Dispose();
    FileStream = null;
    if (Writable)
      RandomDeviceManager.RemoveWritableDevice(SegmentId, Category);
    else
      RandomDeviceManager.RemoveReadOnlyDevice(SegmentId, Category);
  }

  public void Delete()
  {
    BlockCache.Clear();
    Dispose();
    FileStreamProvider.DeleteFile(FilePath);
  }

  public void ClearContent()
  {
    // first 4 bytes hold the block size.
    FileStream.SetLength(MetaDataSize);
    FileStream.Seek(MetaDataSize, SeekOrigin.Begin);

    NextBlockIndex = 0;
    CompressedBlockPositions.Clear();
    CompressedBlockLengths.Clear();
    DecompressedBlockLengths.Clear();
    LastBlockLength = 0;
    BlockCache.Clear();
  }

  public void SealDevice()
  {
    var nextBlock = NextBlock;
    if (nextBlock != null)
    {
      AppendBlock(nextBlock);
      LastBlockLength = nextBlock.Length;
      --NextBlockIndex;
      NextBlock = null;
    }
    WriteCompressedBlockPositionsAndLengths();
  }

  void WriteCompressedBlockPositionsAndLengths()
  {
    var offset = CompressedBlockPositions.LastOrDefault(MetaDataSize);
    offset += CompressedBlockLengths.LastOrDefault();
    FileStream.SetLength(offset);
    FileStream.Position = offset;
    var positions = CompressedBlockPositions;
    var lengths1 = CompressedBlockLengths;
    var lengths2 = DecompressedBlockLengths;
    var len = positions.Count;
    var bw = BinaryWriter;
    for (var i = 0; i < len; ++i)
    {
      bw.Write(positions[i]);
      bw.Write(lengths1[i]);
      bw.Write(lengths2[i]);
    }
    bw.Write(positions.Count);
    bw.Write(offset);
    FileStream.Flush(true);
  }

  (List<long> positions,
   List<int> compressedLengths,
   List<int> decompressedLengths) ReadCompressedBlockPositionsAndLengths()
  {
    FileStream.Seek(-sizeof(int) - sizeof(long), SeekOrigin.End);
    var br = BinaryReader;
    var len = br.ReadInt32();
    var offset = br.ReadInt64();
    FileStream.Seek(offset, SeekOrigin.Begin);
    var positions = new List<long>(len);
    var compressedLengths = new List<int>(len);
    var decompressedLengths = new List<int>(len);
    for (var i = 0; i < len; ++i)
    {
      positions.Add(br.ReadInt64());
      compressedLengths.Add(br.ReadInt32());
      decompressedLengths.Add(br.ReadInt32());
    }
    return (positions, compressedLengths, decompressedLengths);
  }

  public int ReleaseInactiveCachedBuffers(long ticks)
  {
    if (NextBlock != null &&
        NextBlock.LastAccessTicks < ticks)
    {
      NextBlock = null;
    }
    return BlockCache.RemoveBlocksAccessedBefore(ticks);
  }
}

#pragma warning restore CA2213
