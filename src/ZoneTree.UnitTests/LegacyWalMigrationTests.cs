using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class LegacyWalMigrationTests
{
  // Real filesystem fixtures generated with main commit 8347664f5e5d1c98a530e2c15eb63ab42118e9e4
  // (ZoneTree 1.9.9) by the removed optimistic transaction layer: transaction 1 commits
  // {1:10, 2:20}, then rotates the mutable segment; transaction 2 commits {3:30};
  // transaction 3 writes {2:999, 4:40} without committing. That layer wrote in place,
  // so the tree holds {1:10, 2:999, 3:30, 4:40}. Its transaction log WALs stay in the
  // fixture and must be migrated with the rest of the directory.
  // Compression uses 128-byte blocks and CompressionMethod.None to keep fixtures tiny.
  string DataDirectory;

  [SetUp]
  public void SetUp()
  {
    DataDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory,
        nameof(LegacyWalMigrationTests), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(DataDirectory);
  }

  [TearDown]
  public void TearDown()
  {
    Directory.Delete(DataDirectory, true);
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public void LegacyDatabaseMigratesOnce(WriteAheadLogMode mode)
  {
    CopyFixture(mode);
    var originalFiles = LegacyWalFiles();
    var oldWal = WalPath("seg", 2, "wal", mode);
    var oldEntries = ReadEntries(oldWal, mode);
    Assert.That(oldEntries.Any(x => x.Checksum != 0), Is.True);
    var provider = new CountingWalProvider(DataDirectory);
    var factory = CreateFactory(mode).SetWriteAheadLogProvider(_ => provider);

    using (var tree = factory.Open())
    {
      Assert.That(provider.MigrationVersions, Is.EqualTo(new[] { new Version("1.9.9.0") }));
      Assert.That(tree.Maintenance.MutableSegment.SegmentId, Is.EqualTo(2));
      Assert.That(tree.Maintenance.ReadOnlySegments.Count, Is.EqualTo(1));
      AssertValue(tree, 1, 10);
      AssertValue(tree, 2, 999);
      AssertValue(tree, 3, 30);
      AssertValue(tree, 4, 40);
      foreach (var file in originalFiles)
      {
        Assert.That(File.Exists(file), Is.False);
        Assert.That(File.Exists(file.Replace(".wal.", ".wal_crc.", StringComparison.Ordinal)), Is.True);
      }
      Assert.That(Directory.GetFiles(DataDirectory, "*.wal_xxh3.*", SearchOption.AllDirectories), Is.Empty);

      tree.Upsert(5, 50);
    }

    var legacyWal = WalPath("seg", 2, "wal_crc", mode);
    var appendedEntries = ReadEntries(legacyWal, mode);
    Assert.That(appendedEntries.Count, Is.GreaterThan(oldEntries.Count));
    Assert.That(appendedEntries.Take(oldEntries.Count).Select(x => x.Checksum),
        Is.EqualTo(oldEntries.Select(x => x.Checksum)));
    Assert.That(appendedEntries.Skip(oldEntries.Count).Select(x => x.Checksum), Is.All.Zero);

    using (var tree = CreateFactory(mode).Open())
    {
      AssertValue(tree, 2, 999);
      AssertValue(tree, 4, 40);
      AssertValue(tree, 5, 50);
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Upsert(6, 60);
    }

    var newWal = WalPath("seg", 3, "wal_xxh3", mode);
    var newEntries = ReadEntries(newWal, mode);
    Assert.That(newEntries, Is.Not.Empty);
    Assert.That(newEntries.All(x => x.ValidateChecksum()), Is.True);
    using var reopened = CreateFactory(mode).Open();
    AssertValue(reopened, 1, 10);
    AssertValue(reopened, 6, 60);
  }

  [Test]
  public void ProviderSuppliedThroughOptionsMigrates()
  {
    CopyFixture(WriteAheadLogMode.Sync);
    var provider = new CountingWalProvider(DataDirectory);
    var factory = CreateFactory(WriteAheadLogMode.Sync)
        .Configure(options => options.WriteAheadLogProvider = provider);
    using var tree = factory.Open();
    Assert.That(provider.MigrationVersions, Is.EqualTo(new[] { new Version("1.9.9.0") }));
    AssertValue(tree, 2, 999);
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public void PartiallyRenamedDatabaseCompletesMigration(WriteAheadLogMode mode)
  {
    CopyFixture(mode);
    var original = WalPath("seg", 2, "wal", mode);
    File.Move(original, WalPath("seg", 2, "wal_crc", mode));
    // Compressed tails deliberately retain their old names, as after an interrupted rename.
    using var tree = CreateFactory(mode).Open();
    AssertValue(tree, 2, 999);
    Assert.That(LegacyWalFiles(), Is.Empty);
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public void LegacyChecksumIsIgnoredButNewChecksumIsValidated(WriteAheadLogMode mode)
  {
    CopyFixture(mode);
    CorruptChecksum(WalPath("seg", 2, "wal", mode), mode);
    using (var tree = CreateFactory(mode).Open())
    {
      Assert.That(tree.TryGet(2, out var value), Is.True);
      Assert.That(value, Is.EqualTo(999));
      tree.Maintenance.MoveMutableSegmentForward();
      tree.Upsert(6, 60);
    }

    var newWal = WalPath("seg", 3, "wal_xxh3", mode);
    CorruptChecksum(newWal, mode);
    var provider = new WriteAheadLogProvider(new ConsoleLogger(), new LocalFileStreamProvider(), DataDirectory);
    using var wal = provider.GetOrCreateWAL(3, "seg", WalOptions(mode), new Int32Serializer(), new Int32Serializer());
    var result = wal.ReadLogEntries(true, true, false);
    Assert.That(result.Success, Is.False);
    Assert.That(result.Exceptions.Values.Any(x => x.Message.Contains("Checksum failed", StringComparison.Ordinal)), Is.True);
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public void ReplacingLegacyWalKeepsZeroChecksumsAndCanBeReopened(WriteAheadLogMode mode)
  {
    CopyFixture(mode);
    using (CreateFactory(mode).Open()) { }
    var provider = new WriteAheadLogProvider(new ConsoleLogger(), new LocalFileStreamProvider(), DataDirectory);
    using (var wal = provider.GetOrCreateWAL(2, "seg", WalOptions(mode), new Int32Serializer(), new Int32Serializer()))
    {
      wal.ReplaceWriteAheadLog(new[] { 2, 3, 4 }, new[] { 222, 30, 40 }, true);
    }
    var entries = ReadEntries(WalPath("seg", 2, "wal_crc", mode), mode);
    Assert.That(entries.Count, Is.EqualTo(3));
    Assert.That(entries.Select(x => x.Checksum), Is.All.Zero);
    using var tree = CreateFactory(mode).Open();
    Assert.That(tree.TryGet(2, out var value), Is.True);
    Assert.That(value, Is.EqualTo(222));
  }

  ZoneTreeFactory<int, int> CreateFactory(WriteAheadLogMode mode)
  {
    return new ZoneTreeFactory<int, int>()
        .SetDataDirectory(DataDirectory)
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .ConfigureWriteAheadLogOptions(options =>
        {
          options.WriteAheadLogMode = mode;
          options.CompressionBlockSize = 128;
          options.CompressionMethod = CompressionMethod.None;
          options.SyncCompressedModeOptions.EnableTailWriterJob = false;
        });
  }

  static WriteAheadLogOptions WalOptions(WriteAheadLogMode mode)
  {
    var options = new WriteAheadLogOptions
    {
      WriteAheadLogMode = mode,
      CompressionBlockSize = 128,
      CompressionMethod = CompressionMethod.None
    };
    options.SyncCompressedModeOptions.EnableTailWriterJob = false;
    return options;
  }

  void CopyFixture(WriteAheadLogMode mode)
  {
    var source = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "LegacyCrcWals", mode.ToString());
    Assert.That(Directory.Exists(source), Is.True, "Legacy fixtures must be copied to the test output.");
    foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
    {
      var destination = Path.Combine(DataDirectory, Path.GetRelativePath(source, file));
      Directory.CreateDirectory(Path.GetDirectoryName(destination));
      File.Copy(file, destination);
    }
  }

  string WalPath(string category, int segmentId, string format, WriteAheadLogMode mode)
      => Path.Combine(DataDirectory, category, $"{segmentId}.{format}.{(int)mode}");

  string[] LegacyWalFiles()
      => Directory.GetFiles(DataDirectory, "*", SearchOption.AllDirectories)
          .Where(x => Path.GetFileName(x).Contains(".wal.", StringComparison.Ordinal)).ToArray();

  static void AssertValue(IZoneTree<int, int> tree, int key, int expected)
  {
    Assert.That(tree.TryGet(key, out var value), Is.True);
    Assert.That(value, Is.EqualTo(expected));
  }

  static Stream OpenRecordStream(string path, WriteAheadLogMode mode)
      => mode == WriteAheadLogMode.Sync
          ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
          : new CompressedFileStream(new ConsoleLogger(), new LocalFileStreamProvider(), path,
              128, false, 0, CompressionMethod.None, 0);

  static List<LogEntry> ReadEntries(string path, WriteAheadLogMode mode)
  {
    using var stream = OpenRecordStream(path, mode);
    stream.Seek(0, SeekOrigin.Begin);
    using var reader = new BinaryReader(stream);
    var entries = new List<LogEntry>();
    while (stream.Position < stream.Length)
    {
      LogEntry entry = default;
      LogEntry.ReadLogEntry(reader, ref entry);
      entries.Add(entry);
    }
    return entries;
  }

  static void CorruptChecksum(string path, WriteAheadLogMode mode)
  {
    using var stream = OpenRecordStream(path, mode);
    stream.Seek(0, SeekOrigin.Begin);
    using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
    var bytes = reader.ReadBytes((int)stream.Length);
    bytes[^1] ^= 0x80;
    stream.SetLength(0);
    stream.Seek(0, SeekOrigin.Begin);
    stream.Write(bytes, 0, bytes.Length);
  }

  sealed class CountingWalProvider(string directory) : IWriteAheadLogProvider
  {
    readonly WriteAheadLogProvider Inner = new(new ConsoleLogger(), new LocalFileStreamProvider(), directory);
    public List<Version> MigrationVersions { get; } = new();
    public void MigrateWals(Version databaseVersion)
    {
      MigrationVersions.Add(databaseVersion);
      Inner.MigrateWals(databaseVersion);
    }
    public void InitCategory(string category) => Inner.InitCategory(category);
    public IWriteAheadLog<TKey, TValue> GetOrCreateWAL<TKey, TValue>(long segmentId, string category,
        WriteAheadLogOptions options, ISerializer<TKey> keySerializer, ISerializer<TValue> valueSerializer)
        => Inner.GetOrCreateWAL(segmentId, category, options, keySerializer, valueSerializer);
    public IWriteAheadLog<TKey, TValue> GetWAL<TKey, TValue>(long segmentId, string category)
        => Inner.GetWAL<TKey, TValue>(segmentId, category);
    public bool RemoveWAL(long segmentId, string category) => Inner.RemoveWAL(segmentId, category);
    public void DropStore() => Inner.DropStore();
  }
}
