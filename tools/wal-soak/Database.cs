using ZoneTree.AbstractFileStream;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ZoneTree.WalSoak;

static class Database
{
  sealed class Bytes : ISerializer<byte[]>
  {
    public Memory<byte> Serialize(in byte[] value) => value;
    public byte[] Deserialize(Memory<byte> bytes) => bytes.ToArray();
  }

  public static ZoneTreeFactory<long, byte[]> Factory(CaseConfig config, IFileStreamProvider files, Wire log, string? path = null) =>
    new ZoneTreeFactory<long, byte[]>(files)
      .SetDataDirectory(path ?? config.Database)
      .SetLogger(log)
      .SetKeySerializer(new Int64Serializer())
      .SetValueSerializer(new Bytes())
      .Configure(o => { o.AllowUnsafeOptionValues = true; o.DiskSegmentMaxItemCount = 16 * 1024; })
      .SetMutableSegmentMaxItemCount(config.Profile.MutableSize)
      .ConfigureDiskSegmentOptions(o =>
      {
        o.DiskSegmentMode = config.Profile.Disk;
        o.CompressionMethod = config.Profile.Compression;
        o.CompressionBlockSize = 128 * 1024;
        o.MinimumRecordCount = 128;
        o.MaximumRecordCount = 512;
        o.KeyCacheSize = o.ValueCacheSize = 256;
        o.MaterializedEntryCacheSize = 64;
      })
      .ConfigureWriteAheadLogOptions(o =>
      {
        o.WriteAheadLogMode = config.Profile.Wal;
        o.CompressionMethod = config.Profile.Compression;
        o.CompressionBlockSize = config.Profile.BlockSize;
        o.SharedModeOptions.MaximumChunkSize = config.Profile.ChunkSize;
      });

  public static async Task Initialize(ITransactionalZoneTree<long, byte[]> tree, CaseConfig config)
  {
    using var tx = tree.BeginTransaction();
    foreach (var model in LaneModel.Create(config)) foreach (var (key, value) in model.Records) tx.Upsert(key, value);
    await tx.CommitDurableAsync();
  }
}
