using System.Text.Json.Nodes;
using ZoneTree.AbstractFileStream;
using ZoneTree.Exceptions;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

/// <summary>
/// A power loss may tear everything written after the last disk sync.
/// The database must open with every synced record after any such state,
/// and it must still report damage that a power loss cannot explain.
/// </summary>
public sealed class PowerLossRecoveryTests
{
  const string MetaWalPath = "data/0.meta.wal";

  const string MetaFilePath = "data/0.json";

  const int MetaWalRecordSize = 24;

  const int SectorSize = 512;

  // The default WAL block size, and a record count where
  // record 9362 of 28 bytes spans block 0 and block 1.
  const int RecordsPerBlock = 9362;

  static ZoneTreeFactory<int, int> Factory(IFileStreamProvider provider, WriteAheadLogMode mode)
  {
    return new ZoneTreeFactory<int, int>(provider)
        .SetDataDirectory("data")
        .SetLogger(new ConsoleLogger(LogLevel.Error))
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = mode);
  }

  static ZoneTreeFactory<int, int> SmallBlockFactory(
      IFileStreamProvider provider, WriteAheadLogMode mode, CompressionMethod compressionMethod = CompressionMethod.Zstd)
  {
    return Factory(provider, mode)
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .ConfigureWriteAheadLogOptions(options =>
        {
          options.CompressionBlockSize = 2048;
          options.CompressionMethod = compressionMethod;
        });
  }

  static void Overwrite(IFileStreamProvider provider, string path, byte[] bytes)
  {
    using var stream = provider.CreateFileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    stream.Write(bytes, 0, bytes.Length);
    stream.Flush(true);
  }

  static TProvider Copy<TProvider>(IFileStreamProvider source, TProvider copy)
      where TProvider : IFileStreamProvider
  {
    void CopyDirectory(string directory)
    {
      copy.CreateDirectory(directory);
      foreach (var file in source.GetFiles(directory))
        Overwrite(copy, file, source.ReadAllBytes(file));
      foreach (var subDirectory in source.GetDirectories(directory))
        CopyDirectory(subDirectory);
    }
    CopyDirectory("data");
    return copy;
  }

  static InMemoryFileStreamProvider Copy(IFileStreamProvider source)
  {
    return Copy(source, new InMemoryFileStreamProvider());
  }

  static string MainWalPath(IFileStreamProvider provider)
  {
    return provider.GetFiles("data/seg")
        .Single(x => x.Contains(".wal", StringComparison.Ordinal) && !x.EndsWith(".tail", StringComparison.Ordinal));
  }

  static int[] MissingKeys(IZoneTree<int, int> tree, int count)
  {
    return Enumerable.Range(0, count)
        .Where(key => !tree.TryGet(key, out var value) || value != key + 1)
        .ToArray();
  }

  static void UpsertKeys(IZoneTree<int, int> tree, int from, int to)
  {
    for (var key = from; key < to; ++key)
      tree.Upsert(key, key + 1);
  }

  /// <summary>
  /// Creates the bytes of valid records, as a later persisted sector holds them.
  /// </summary>
  static byte[] CreateValidRecords(int fromKey, int toKey)
  {
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    var serializer = new Int32Serializer();
    for (var key = fromKey; key < toKey; ++key)
    {
      var value = key + 1;
      LogEntry.AppendLogEntry(writer, serializer.Serialize(key), serializer.Serialize(value), key + 1);
    }
    writer.Flush();
    return stream.ToArray();
  }

  // Issue 204

  [Test]
  public void DurableFileWriterDoesNotKeepTheTrailingBytesOfAStaleTempFile()
  {
    var provider = new InMemoryFileStreamProvider();
    Overwrite(provider, "data/meta.json.tmp", "{\"a\":1234567890}"u8.ToArray());

    var bytes = "{\"a\":1}"u8.ToArray();
    provider.GetDurableFileWriter().WriteAllBytes("data/meta.json", bytes);

    Assert.That(provider.ReadAllBytes("data/meta.json"), Is.EqualTo(bytes));
  }

  [Test]
  public void TreeOpensAfterItsMetaFileShrinksOverAStaleTempFile()
  {
    var provider = new InMemoryFileStreamProvider();
    using (var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate())
      tree.Upsert(0, 1);
    var longerMetaFile = provider.ReadAllBytes(MetaFilePath).Concat(new byte[64]).ToArray();
    Overwrite(provider, MetaFilePath + ".tmp", longerMetaFile);

    using (Factory(provider, WriteAheadLogMode.Sync).Open())
    {
    }
    using var reopened = Factory(provider, WriteAheadLogMode.Sync).Open();
    Assert.That(MissingKeys(reopened, 1), Is.Empty);
  }

  // Issue 201

  [Test]
  public void PowerLossAfterSaveMetaDataKeepsTreeOpenable()
  {
    var provider = new InMemoryFileStreamProvider();
    byte[] metaWalBeforeSave;
    using (var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 100);
      tree.Maintenance.MoveMutableSegmentForward();
      metaWalBeforeSave = provider.ReadAllBytes(MetaWalPath);
      tree.Maintenance.SaveMetaData();
    }
    // The meta file was replaced durably, but clearing the meta WAL was not.
    Overwrite(provider, MetaWalPath, metaWalBeforeSave);

    using var reopened = Factory(provider, WriteAheadLogMode.Sync).Open();
    Assert.That(MissingKeys(reopened, 100), Is.Empty);
  }

  [Test]
  public void PowerLossDuringSaveMetaDataKeepsTheRecordsOfTheNewMetaWal()
  {
    var provider = new InMemoryFileStreamProvider();
    byte[] metaWalBeforeSave;
    using (var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 100);
      tree.Maintenance.MoveMutableSegmentForward();
      metaWalBeforeSave = provider.ReadAllBytes(MetaWalPath);
      tree.Maintenance.SaveMetaData();
      UpsertKeys(tree, 100, 200);
      tree.Maintenance.MoveMutableSegmentForward();
    }
    // A sector mix: the new records are at the start of the file, the old ones follow.
    var newRecords = provider.ReadAllBytes(MetaWalPath);
    Assert.That(metaWalBeforeSave.Length, Is.GreaterThan(0));
    Overwrite(provider, MetaWalPath, newRecords.Concat(metaWalBeforeSave).ToArray());

    using var reopened = Factory(provider, WriteAheadLogMode.Sync).Open();
    Assert.That(MissingKeys(reopened, 200), Is.Empty);
    Assert.That(reopened.Maintenance.ReadOnlySegments, Has.Count.EqualTo(2));
  }

  [TestCase(MetaWalRecordSize)]
  [TestCase(MetaWalRecordSize / 2)]
  [TestCase(MetaWalRecordSize * 3)]
  public void TornMetaWalTailKeepsTreeOpenable(int zeroFilledLength)
  {
    var provider = new InMemoryFileStreamProvider();
    using (var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 100);
      tree.Maintenance.MoveMutableSegmentForward();
    }
    var metaWal = provider.ReadAllBytes(MetaWalPath);
    Overwrite(provider, MetaWalPath, metaWal.Concat(new byte[zeroFilledLength]).ToArray());

    using var reopened = Factory(provider, WriteAheadLogMode.Sync).Open();
    Assert.That(MissingKeys(reopened, 100), Is.Empty);
    Assert.That(reopened.Maintenance.ReadOnlySegments, Has.Count.EqualTo(1));
  }

  [Test]
  public void CorruptedMetaWalRecordFollowedByValidRecordIsReported()
  {
    var provider = new InMemoryFileStreamProvider();
    using (var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 100);
      tree.Maintenance.MoveMutableSegmentForward();
    }
    var metaWal = provider.ReadAllBytes(MetaWalPath);
    Assert.That(metaWal.Length, Is.GreaterThanOrEqualTo(2 * MetaWalRecordSize));
    metaWal[8] ^= 0x40;
    Overwrite(provider, MetaWalPath, metaWal);

    Assert.Throws<ZoneTreeMetaCorruptionException>(() => Factory(provider, WriteAheadLogMode.Sync).Open());
  }

  [Test]
  public void MetaWalRecordsNewerThanTheMetaFileAreReported()
  {
    var provider = new InMemoryFileStreamProvider();
    byte[] olderMetaFile;
    using (var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 100);
      olderMetaFile = provider.ReadAllBytes(MetaFilePath);
      tree.Maintenance.SaveMetaData();
      UpsertKeys(tree, 100, 200);
      tree.Maintenance.MoveMutableSegmentForward();
    }
    // The rename of the newer meta file is lost, or an older copy is restored.
    Overwrite(provider, MetaFilePath, olderMetaFile);

    Assert.Throws<ZoneTreeMetaCorruptionException>(() => Factory(provider, WriteAheadLogMode.Sync).Open());
  }

  [Test]
  public void MetaWalRecordsOfAnOlderVersionAreReplayed()
  {
    var provider = new InMemoryFileStreamProvider();
    using (var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 100);
      tree.Maintenance.MoveMutableSegmentForward();
    }
    // Older versions leave these bytes zero and do not write the generation.
    var metaWal = provider.ReadAllBytes(MetaWalPath);
    for (var offset = 0; offset < metaWal.Length; offset += MetaWalRecordSize)
    {
      Array.Clear(metaWal, offset + 4, sizeof(int));
      Array.Clear(metaWal, offset + 20, sizeof(int));
    }
    Overwrite(provider, MetaWalPath, metaWal);
    var meta = JsonNode.Parse(provider.ReadAllBytes(MetaFilePath)).AsObject();
    Assert.That(meta.Remove("MetaWalGeneration"), Is.True);
    Overwrite(provider, MetaFilePath, System.Text.Encoding.UTF8.GetBytes(meta.ToJsonString()));

    using var reopened = Factory(provider, WriteAheadLogMode.Sync).Open();
    Assert.That(MissingKeys(reopened, 100), Is.Empty);
    Assert.That(reopened.Maintenance.ReadOnlySegments, Has.Count.EqualTo(1));
  }

  // Issue 209

  /// <summary>
  /// A tree whose metadata is saved and whose meta WAL is empty.
  /// </summary>
  static InMemoryFileStreamProvider CreateSettledTree()
  {
    var provider = new InMemoryFileStreamProvider();
    using var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate();
    UpsertKeys(tree, 0, 100);
    tree.Maintenance.MoveMutableSegmentForward();
    tree.Maintenance.SaveMetaData();
    return provider;
  }

  [Test]
  public void ReopeningASettledTreeDoesNotRewriteTheMetadata()
  {
    var provider = CreateSettledTree();
    Assert.That(provider.ReadAllBytes(MetaWalPath), Is.Empty);
    var metaFile = provider.ReadAllBytes(MetaFilePath);

    for (var i = 0; i < 3; ++i)
    {
      using (var tree = Factory(provider, WriteAheadLogMode.Sync).Open())
        Assert.That(MissingKeys(tree, 100), Is.Empty);
      Assert.That(provider.ReadAllBytes(MetaFilePath), Is.EqualTo(metaFile), $"reopen {i}");
    }
  }

  [TestCase(MetaWalRecordSize / 2)]
  [TestCase(MetaWalRecordSize)]
  public void ATornMetaWalIsClearedOnReopenBeforeLaterAppends(int tornLength)
  {
    var provider = CreateSettledTree();
    Overwrite(provider, MetaWalPath, new byte[tornLength]);

    using (var tree = Factory(provider, WriteAheadLogMode.Sync).Open())
    {
      Assert.That(provider.ReadAllBytes(MetaWalPath), Is.Empty);
      UpsertKeys(tree, 100, 200);
      tree.Maintenance.MoveMutableSegmentForward();
    }

    // Records that follow an invalid record would be reported as corruption.
    using var reopened = Factory(provider, WriteAheadLogMode.Sync).Open();
    Assert.That(MissingKeys(reopened, 200), Is.Empty);
  }

  // Issue 202

  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  public async Task RecoveredRecordsSurviveAPowerLossAfterTruncatingACommittedBlock(WriteAheadLogMode mode)
  {
    var provider = new InMemoryFileStreamProvider();
    using (var tree = Factory(provider, mode).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 9400);
      await tree.SyncWALAsync();
    }
    // First power loss: block 0 is committed, the tail is not rewritten for block 1.
    var mainPath = MainWalPath(provider);
    Overwrite(provider, mainPath + ".tail", new byte[8]);
    var mainBeforeRecovery = provider.ReadAllBytes(mainPath);

    using var recovered = Factory(provider, mode).Open();
    Assert.That(MissingKeys(recovered, RecordsPerBlock), Is.Empty);

    // Second power loss: the new file size survives, unsynced content does not.
    var mainAfterRecovery = provider.ReadAllBytes(mainPath);
    var image = Copy(provider);
    Overwrite(image, mainPath, mainBeforeRecovery[..mainAfterRecovery.Length]);

    using var reopened = Factory(image, mode).Open();
    Assert.That(MissingKeys(reopened, RecordsPerBlock), Is.Empty);
  }

  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  public async Task RecoveryKeepsTheCommittedBlockUntilItsReplacementIsDurable(WriteAheadLogMode mode)
  {
    var provider = new PowerLossFileStreamProvider();
    using (var tree = Factory(provider, mode).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 9400);
      await tree.SyncWALAsync();
    }
    var mainPath = MainWalPath(provider);
    Overwrite(provider, mainPath + ".tail", new byte[8]);

    // Every state that recovery makes durable holds every recovered record.
    var images = new List<PowerLossFileStreamProvider>();
    provider.OnDurableChange = () =>
    {
      lock (images)
        images.Add(provider.SimulatePowerLoss());
    };
    using (Factory(provider, mode).Open())
    {
    }
    provider.OnDurableChange = null;
    Assert.That(images, Is.Not.Empty);
    foreach (var image in images)
    {
      using var reopened = Factory(image, mode).Open();
      Assert.That(MissingKeys(reopened, RecordsPerBlock), Is.Empty);
    }
  }

  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  public async Task WritesAfterTruncatingACommittedBlockSurviveAPowerLoss(WriteAheadLogMode mode)
  {
    var provider = new PowerLossFileStreamProvider();
    using (var tree = Factory(provider, mode).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 9400);
      await tree.SyncWALAsync();
    }
    var mainPath = MainWalPath(provider);
    Overwrite(provider, mainPath + ".tail", new byte[8]);

    using var recovered = Factory(provider, mode).Open();
    UpsertKeys(recovered, RecordsPerBlock, 2 * RecordsPerBlock + 100);
    await recovered.SyncWALAsync();

    using var reopened = Factory(provider.SimulatePowerLoss(), mode).Open();
    Assert.That(MissingKeys(reopened, 2 * RecordsPerBlock + 100), Is.Empty);
  }

  // Issue 203

  [Test]
  public async Task SyncWalOpensWithAZeroFilledTail()
  {
    var provider = new InMemoryFileStreamProvider();
    using (var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 10);
      await tree.SyncWALAsync();
    }
    var mainPath = MainWalPath(provider);
    var synced = provider.ReadAllBytes(mainPath);
    Overwrite(provider, mainPath, synced.Concat(new byte[SectorSize]).ToArray());

    using (var reopened = Factory(provider, WriteAheadLogMode.Sync).Open())
    {
      Assert.That(MissingKeys(reopened, 10), Is.Empty);
      UpsertKeys(reopened, 10, 20);
    }
    using var again = Factory(provider, WriteAheadLogMode.Sync).Open();
    Assert.That(MissingKeys(again, 20), Is.Empty);
  }

  [Test]
  public async Task SyncWalDropsPersistedRecordsAfterAnUnsyncedSector()
  {
    var provider = new InMemoryFileStreamProvider();
    using (var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 10);
      await tree.SyncWALAsync();
    }
    var mainPath = MainWalPath(provider);
    var synced = provider.ReadAllBytes(mainPath);
    // The rest of the last synced sector is lost, and the next sector persists.
    var image = synced
        .Concat(new byte[SectorSize - synced.Length % SectorSize])
        .Concat(CreateValidRecords(10, 15))
        .ToArray();
    Overwrite(provider, mainPath, image);

    using var reopened = Factory(provider, WriteAheadLogMode.Sync).Open();
    Assert.That(MissingKeys(reopened, 10), Is.Empty);
    Assert.That(reopened.ContainsKey(10), Is.False);
    Assert.That(provider.ReadAllBytes(mainPath), Is.EqualTo(synced));
  }

  [Test]
  public async Task SyncWalTruncationIsDurableBeforeNewRecordsFollowIt()
  {
    var provider = new PowerLossFileStreamProvider();
    using (var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 10);
      await tree.SyncWALAsync();
    }
    var mainPath = MainWalPath(provider);
    var synced = provider.ReadAllBytes(mainPath);
    Overwrite(provider, mainPath, synced
        .Concat(new byte[SectorSize - synced.Length % SectorSize])
        .Concat(CreateValidRecords(10, 15))
        .ToArray());

    // Sectors of these valid records must not come back in
    // the middle of new records, where they would look like a corruption.
    using var reopened = Factory(provider, WriteAheadLogMode.Sync).Open();
    Assert.That(provider.SimulatePowerLoss().ReadAllBytes(mainPath), Is.EqualTo(synced));
  }

  [TestCase(4)]
  [TestCase(8)]
  public async Task SyncWalReportsACorruptedRecordFollowedByValidRecords(int corruptedOffset)
  {
    var provider = new InMemoryFileStreamProvider();
    using (var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 10);
      await tree.SyncWALAsync();
    }
    var mainPath = MainWalPath(provider);
    var bytes = provider.ReadAllBytes(mainPath);
    // 4: the operation index fails the checksum. 8: the key length runs past the file.
    bytes[5 * 28 + corruptedOffset + 3] ^= 0x7F;
    Overwrite(provider, mainPath, bytes);

    Assert.Throws<WriteAheadLogCorruptionException>(() => Factory(provider, WriteAheadLogMode.Sync).Open());
    Assert.That(provider.ReadAllBytes(mainPath), Is.EqualTo(bytes));
  }

  /// <summary>
  /// Creates a record larger than the search window of the Sync WAL recovery.
  /// </summary>
  static byte[] CreateRecordLargerThanTheSearchWindow(int declaredPayloadLength, int writtenPayloadLength)
  {
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(1000L);
    writer.Write(sizeof(int));
    writer.Write(declaredPayloadLength);
    writer.Write(Enumerable.Repeat((byte)0x5A, writtenPayloadLength).ToArray());
    writer.Flush();
    return stream.ToArray();
  }

  [Test]
  public async Task SyncWalReportsDamageWhenTheSearchWindowEndsBeforeTheLog()
  {
    const int payloadLength = 33 * 1024 * 1024;
    var provider = new InMemoryFileStreamProvider();
    using (var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 10);
      await tree.SyncWALAsync();
    }
    // The whole record is present but fails its checksum, and a valid record follows.
    var mainPath = MainWalPath(provider);
    var bytes = provider.ReadAllBytes(mainPath)
        .Concat(CreateRecordLargerThanTheSearchWindow(payloadLength, payloadLength + sizeof(int) + sizeof(uint)))
        .Concat(CreateValidRecords(10, 11))
        .ToArray();
    Overwrite(provider, mainPath, bytes);

    Assert.Throws<WriteAheadLogCorruptionException>(() => Factory(provider, WriteAheadLogMode.Sync).Open());
    Assert.That(provider.ReadAllBytes(mainPath), Is.EqualTo(bytes));
  }

  [Test]
  public async Task SyncWalTruncatesACutOffRecordLargerThanTheSearchWindow()
  {
    const int payloadLength = 33 * 1024 * 1024;
    var provider = new InMemoryFileStreamProvider();
    using (var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 10);
      await tree.SyncWALAsync();
    }
    var mainPath = MainWalPath(provider);
    var synced = provider.ReadAllBytes(mainPath);
    Overwrite(provider, mainPath, synced
        .Concat(CreateRecordLargerThanTheSearchWindow(2 * payloadLength, payloadLength))
        .ToArray());

    using var reopened = Factory(provider, WriteAheadLogMode.Sync).Open();
    Assert.That(MissingKeys(reopened, 10), Is.Empty);
    Assert.That(provider.ReadAllBytes(mainPath), Is.EqualTo(synced));
  }

  [Test]
  public async Task SyncWalTruncatesACorruptedLastRecord()
  {
    var provider = new InMemoryFileStreamProvider();
    using (var tree = Factory(provider, WriteAheadLogMode.Sync).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 10);
      await tree.SyncWALAsync();
    }
    var mainPath = MainWalPath(provider);
    var bytes = provider.ReadAllBytes(mainPath);
    bytes[^1] ^= 0x40;
    Overwrite(provider, mainPath, bytes);

    using var reopened = Factory(provider, WriteAheadLogMode.Sync).Open();
    Assert.That(MissingKeys(reopened, 9), Is.Empty);
    Assert.That(reopened.ContainsKey(9), Is.False);
  }

  // Issue 205

  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  public async Task CompressedWalOpensWhenTheTailLengthPersistsWithoutItsData(WriteAheadLogMode mode)
  {
    var provider = new InMemoryFileStreamProvider();
    using var tree = Factory(provider, mode).OpenOrCreate();
    UpsertKeys(tree, 0, 50);
    await tree.SyncWALAsync();

    var image = Copy(provider);
    var tailPath = image.GetFiles("data/seg").Single(x => x.EndsWith(".tail", StringComparison.Ordinal));
    var tail = image.ReadAllBytes(tailPath);
    BitConverter.GetBytes(BitConverter.ToInt32(tail, 4) + SectorSize).CopyTo(tail, 4);
    Overwrite(image, tailPath, tail.Concat(new byte[SectorSize]).ToArray());

    using (var reopened = Factory(image, mode).Open())
    {
      Assert.That(MissingKeys(reopened, 50), Is.Empty);
      UpsertKeys(reopened, 50, 60);
    }
    using var again = Factory(image, mode).Open();
    Assert.That(MissingKeys(again, 60), Is.Empty);
  }

  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  public async Task CompressedWalDropsPersistedRecordsAfterAnUnsyncedTailSector(WriteAheadLogMode mode)
  {
    var provider = new InMemoryFileStreamProvider();
    using var tree = Factory(provider, mode).OpenOrCreate();
    UpsertKeys(tree, 0, 50);
    await tree.SyncWALAsync();

    var image = Copy(provider);
    var tailPath = image.GetFiles("data/seg").Single(x => x.EndsWith(".tail", StringComparison.Ordinal));
    var synced = image.ReadAllBytes(tailPath);
    var unsynced = new byte[2 * SectorSize - synced.Length % SectorSize].Concat(CreateValidRecords(50, 60)).ToArray();
    var torn = synced.Concat(unsynced).ToArray();
    BitConverter.GetBytes(torn.Length - 8).CopyTo(torn, 4);
    Overwrite(image, tailPath, torn);

    using var reopened = Factory(image, mode).Open();
    Assert.That(MissingKeys(reopened, 50), Is.Empty);
    Assert.That(reopened.ContainsKey(50), Is.False);
  }

  [TestCase(WriteAheadLogMode.AsyncCompressed, CompressionMethod.Zstd)]
  [TestCase(WriteAheadLogMode.SyncCompressed, CompressionMethod.Zstd)]
  [TestCase(WriteAheadLogMode.SyncCompressed, CompressionMethod.None)]
  public async Task CompressedWalReportsACorruptedCommittedBlock(WriteAheadLogMode mode, CompressionMethod compressionMethod)
  {
    var provider = new InMemoryFileStreamProvider();
    using (var tree = SmallBlockFactory(provider, mode, compressionMethod).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 1000);
      await tree.SyncWALAsync();
    }
    var mainPath = MainWalPath(provider);
    var bytes = provider.ReadAllBytes(mainPath);
    // Inside the first of many committed blocks.
    bytes[1 + 12 + 40] ^= 0x7F;
    Overwrite(provider, mainPath, bytes);

    Assert.Throws<WriteAheadLogCorruptionException>(() => SmallBlockFactory(provider, mode, compressionMethod).Open());
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public async Task RecoveryFromATornTailWorksWithLocalFiles(WriteAheadLogMode mode)
  {
    var dataPath = $"data/RecoveryFromATornTailWorksWithLocalFiles{mode}";
    if (Directory.Exists(dataPath))
      Directory.Delete(dataPath, true);
    ZoneTreeFactory<int, int> LocalFactory() => new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .SetLogger(new ConsoleLogger(LogLevel.Error))
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = mode);

    using (var tree = LocalFactory().OpenOrCreate())
    {
      UpsertKeys(tree, 0, 50);
      await tree.SyncWALAsync();
    }
    var wal = Directory.GetFiles(Path.Combine(dataPath, "seg"), "*.wal*")
        .Single(path => mode == WriteAheadLogMode.Sync || path.EndsWith(".tail", StringComparison.Ordinal));
    var bytes = File.ReadAllBytes(wal);
    if (mode != WriteAheadLogMode.Sync)
      BitConverter.GetBytes(BitConverter.ToInt32(bytes, 4) + 2 * SectorSize).CopyTo(bytes, 4);
    File.WriteAllBytes(wal, bytes.Concat(new byte[2 * SectorSize]).ToArray());

    using (var recovered = LocalFactory().Open())
    {
      Assert.That(MissingKeys(recovered, 50), Is.Empty);
      UpsertKeys(recovered, 50, 60);
    }
    using var reopened = LocalFactory().Open();
    Assert.That(MissingKeys(reopened, 60), Is.Empty);
    reopened.Maintenance.Drop();
  }

  // A record that passes its checksum is not a torn write, wherever it is.

  sealed class SwitchableInt32Serializer : ISerializer<int>
  {
    public static volatile bool FailDeserialize;

    public int Deserialize(Memory<byte> bytes)
    {
      if (FailDeserialize)
        throw new InvalidOperationException("The serializer cannot read this value.");
      return BinarySerializerHelper.FromByteArray<int>(bytes);
    }

    public Memory<byte> Serialize(in int entry) => BinarySerializerHelper.ToByteArray(entry);
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public async Task DeserializationFailureOfAValidRecordDoesNotTruncateTheWal(WriteAheadLogMode mode)
  {
    ZoneTreeFactory<int, int> SerializerFactory(IFileStreamProvider provider)
        => Factory(provider, mode).SetValueSerializer(new SwitchableInt32Serializer());

    var provider = new InMemoryFileStreamProvider();
    using (var tree = SerializerFactory(provider).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 10);
      await tree.SyncWALAsync();
    }
    SwitchableInt32Serializer.FailDeserialize = true;
    try
    {
      Assert.Throws<WriteAheadLogCorruptionException>(() => SerializerFactory(provider).Open());
    }
    finally
    {
      SwitchableInt32Serializer.FailDeserialize = false;
    }
    using var reopened = SerializerFactory(provider).Open();
    Assert.That(MissingKeys(reopened, 10), Is.Empty);
  }

  // A power loss during a block commit

  [TestCase(WriteAheadLogMode.SyncCompressed, CompressionMethod.Zstd, 0)]
  [TestCase(WriteAheadLogMode.SyncCompressed, CompressionMethod.Zstd, SectorSize)]
  [TestCase(WriteAheadLogMode.AsyncCompressed, CompressionMethod.Zstd, 0)]
  [TestCase(WriteAheadLogMode.AsyncCompressed, CompressionMethod.Zstd, SectorSize)]
  [TestCase(WriteAheadLogMode.SyncCompressed, CompressionMethod.None, 0)]
  [TestCase(WriteAheadLogMode.SyncCompressed, CompressionMethod.None, SectorSize)]
  [TestCase(WriteAheadLogMode.SyncCompressed, CompressionMethod.LZ4, SectorSize)]
  public async Task TornBlockCommitKeepsTheRecordsThatTheTailHolds(
      WriteAheadLogMode mode, CompressionMethod compressionMethod, int persistedLength)
  {
    const int syncedCount = 60;
    var provider = new InMemoryFileStreamProvider();
    using var tree = SmallBlockFactory(provider, mode, compressionMethod).OpenOrCreate();
    UpsertKeys(tree, 0, syncedCount);
    await tree.SyncWALAsync();
    var mainPath = MainWalPath(provider);
    var syncedTail = provider.ReadAllBytes(mainPath + ".tail");

    // The next block commit extends the main file. Its first sector is all that
    // persists, the tail file still holds the synced part of the block.
    UpsertKeys(tree, syncedCount, 200);
    await tree.SyncWALAsync();
    var image = Copy(provider);
    var main = image.ReadAllBytes(mainPath);
    Assert.That(main.Length, Is.GreaterThan(persistedLength + 1));
    var persistedEnd = Math.Max(1, persistedLength);
    Array.Clear(main, persistedEnd, main.Length - persistedEnd);
    Overwrite(image, mainPath, main);
    Overwrite(image, mainPath + ".tail", syncedTail);

    using var reopened = SmallBlockFactory(image, mode, compressionMethod).Open();
    Assert.That(MissingKeys(reopened, syncedCount), Is.Empty);
  }

  [Test]
  public async Task BlockHeaderWithASmallerIndexIsTorn()
  {
    const WriteAheadLogMode mode = WriteAheadLogMode.SyncCompressed;
    var provider = new InMemoryFileStreamProvider();
    using (var tree = SmallBlockFactory(provider, mode).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 200);
      await tree.SyncWALAsync();
    }
    // The sector with the start of the header did not persist, so the index
    // reads as zero. The tail file names a later block than this one seems to be.
    var mainPath = MainWalPath(provider);
    var header = new[] { 0, 16, 2048 }.SelectMany(BitConverter.GetBytes);
    Overwrite(provider, mainPath, provider.ReadAllBytes(mainPath)
        .Concat(header)
        .Concat(Enumerable.Repeat((byte)0x5A, 16))
        .ToArray());

    using var reopened = SmallBlockFactory(provider, mode).Open();
    Assert.That(MissingKeys(reopened, 200), Is.Empty);
  }

  [Test]
  public async Task CorruptedBlockIndexInTheMiddleOfTheFileDoesNotDropBlocks()
  {
    const WriteAheadLogMode mode = WriteAheadLogMode.SyncCompressed;
    var provider = new InMemoryFileStreamProvider();
    using (var tree = SmallBlockFactory(provider, mode).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 500);
      await tree.SyncWALAsync();
    }
    // The tail file names a later block, so block 1 was synced and is not torn.
    var mainPath = MainWalPath(provider);
    var main = provider.ReadAllBytes(mainPath);
    var block1 = 1 + 3 * sizeof(int) + BitConverter.ToInt32(main, 1 + sizeof(int));
    Assert.That(BitConverter.ToInt32(main, block1), Is.EqualTo(1));
    Array.Clear(main, block1, sizeof(int));
    Overwrite(provider, mainPath, main);

    using var reopened = SmallBlockFactory(provider, mode).Open();
    Assert.That(MissingKeys(reopened, 500), Is.Empty);
    Assert.That(provider.ReadAllBytes(mainPath), Is.EqualTo(main));
  }

  [TestCase(WriteAheadLogMode.SyncCompressed, CompressionMethod.LZ4, int.MaxValue)]
  [TestCase(WriteAheadLogMode.SyncCompressed, CompressionMethod.Zstd, 5)]
  [TestCase(WriteAheadLogMode.AsyncCompressed, CompressionMethod.Zstd, 5)]
  public async Task TornFirstCommitAfterAWalReplacementKeepsSyncedRecords(
      WriteAheadLogMode mode, CompressionMethod compressionMethod, int zeroedLength)
  {
    const int syncedCount = 110;
    ZoneTreeFactory<int, int> GarbageCollectingFactory(IFileStreamProvider provider)
        => SmallBlockFactory(provider, mode, compressionMethod)
            .Configure(options => options.EnableSingleSegmentGarbageCollection = true);

    var provider = new InMemoryFileStreamProvider();
    using (var tree = GarbageCollectingFactory(provider).OpenOrCreate())
    {
      for (var round = 0; round < 8; ++round)
        UpsertKeys(tree, 0, 50);
    }

    // Opening replaces the WAL with fewer blocks than the tail index counted.
    string mainPath;
    byte[] syncedTail;
    int commitStart;
    using (var tree = GarbageCollectingFactory(provider).Open())
    {
      mainPath = MainWalPath(provider);
      UpsertKeys(tree, 50, syncedCount);
      await tree.SyncWALAsync();
      syncedTail = provider.ReadAllBytes(mainPath + ".tail");
      commitStart = provider.ReadAllBytes(mainPath).Length;
      UpsertKeys(tree, syncedCount, 180);
      await tree.SyncWALAsync();
    }

    var image = Copy(provider);
    var main = image.ReadAllBytes(mainPath);
    Assert.That(main.Length, Is.GreaterThan(commitStart));
    Array.Clear(main, commitStart, Math.Min(zeroedLength, main.Length - commitStart));
    Overwrite(image, mainPath, main);
    Overwrite(image, mainPath + ".tail", syncedTail);

    using var reopened = GarbageCollectingFactory(image).Open();
    Assert.That(MissingKeys(reopened, syncedCount), Is.Empty);
  }

  sealed class RecordingLogger : ILogger
  {
    readonly List<string> Warnings = new();

    public LogLevel LogLevel { get; set; } = LogLevel.Warning;

    public string[] GetWarnings()
    {
      lock (Warnings)
        return Warnings.ToArray();
    }

    public void LogError(Exception log)
    {
    }

    public void LogWarning(object log)
    {
      lock (Warnings)
        Warnings.Add(log.ToString());
    }

    public void LogInfo(object log)
    {
    }

    public void LogTrace(object log)
    {
    }
  }

  [TestCase(1000)]
  [TestCase(5)]
  public async Task TruncatingTheMainFileOnOpenLogsAWarning(int tornLength)
  {
    const WriteAheadLogMode mode = WriteAheadLogMode.SyncCompressed;
    var provider = new InMemoryFileStreamProvider();
    using (var tree = SmallBlockFactory(provider, mode).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 60);
      await tree.SyncWALAsync();
    }
    var mainPath = MainWalPath(provider);
    var main = provider.ReadAllBytes(mainPath);

    // A clean open truncates nothing.
    var cleanLogger = new RecordingLogger();
    using (SmallBlockFactory(provider, mode).SetLogger(cleanLogger).Open())
    {
    }
    Assert.That(cleanLogger.GetWarnings(), Is.Empty);

    // 1000: a torn block that does not decompress. 5: a partly written header.
    var torn = tornLength == 1000
        ? new[] { 0, 1000, 2048 }.SelectMany(BitConverter.GetBytes).Concat(new byte[1000]).ToArray()
        : new byte[tornLength];
    Overwrite(provider, mainPath, main.Concat(torn).ToArray());

    var logger = new RecordingLogger();
    using var reopened = SmallBlockFactory(provider, mode).SetLogger(logger).Open();
    Assert.That(MissingKeys(reopened, 60), Is.Empty);
    Assert.That(logger.GetWarnings(), Has.Some.Contains(Path.GetFileName(mainPath)));
  }

  [TestCase(WriteAheadLogMode.SyncCompressed, CompressionMethod.Zstd)]
  [TestCase(WriteAheadLogMode.AsyncCompressed, CompressionMethod.Zstd)]
  [TestCase(WriteAheadLogMode.SyncCompressed, CompressionMethod.None)]
  public async Task BlockHeaderWithOnlyItsIndexPersistedIsTorn(
      WriteAheadLogMode mode, CompressionMethod compressionMethod)
  {
    const int syncedCount = 100;
    var provider = new InMemoryFileStreamProvider();
    using (var tree = SmallBlockFactory(provider, mode, compressionMethod).OpenOrCreate())
    {
      UpsertKeys(tree, 0, syncedCount);
      await tree.SyncWALAsync();
    }
    // The commit of the block that the tail names persisted the index,
    // and the sector with the rest of the header did not persist.
    var mainPath = MainWalPath(provider);
    var tail = provider.ReadAllBytes(mainPath + ".tail");
    var header = new[] { BitConverter.ToInt32(tail, 0), 0, 0 }.SelectMany(BitConverter.GetBytes);
    Overwrite(provider, mainPath, provider.ReadAllBytes(mainPath).Concat(header).ToArray());

    using var reopened = SmallBlockFactory(provider, mode, compressionMethod).Open();
    Assert.That(MissingKeys(reopened, syncedCount), Is.Empty);
  }

  [Test]
  public async Task RecordCutOffBeforeAnEmptyLastBlockIsATornTail()
  {
    const WriteAheadLogMode mode = WriteAheadLogMode.SyncCompressed;
    var provider = new InMemoryFileStreamProvider();
    using (var tree = SmallBlockFactory(provider, mode, CompressionMethod.None).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 100);
      await tree.SyncWALAsync();
    }
    // Older versions could commit an empty block, which leaves no content in the
    // last block. The record that spans block 0 and the lost tail is cut off.
    var mainPath = MainWalPath(provider);
    var header = new[] { 1, 0, 0 }.SelectMany(BitConverter.GetBytes);
    Overwrite(provider, mainPath, provider.ReadAllBytes(mainPath).Concat(header).ToArray());
    Overwrite(provider, mainPath + ".tail", BitConverter.GetBytes(2).Concat(new byte[sizeof(int)]).ToArray());

    using var reopened = SmallBlockFactory(provider, mode, CompressionMethod.None).Open();
    Assert.That(MissingKeys(reopened, 2048 / 28), Is.Empty);
  }

  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public async Task DroppingATornBlockIsDurableBeforeNewBlocksFollowIt(WriteAheadLogMode mode)
  {
    var provider = new PowerLossFileStreamProvider();
    using (var tree = SmallBlockFactory(provider, mode).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 60);
      await tree.SyncWALAsync();
    }
    var mainPath = MainWalPath(provider);
    var main = provider.ReadAllBytes(mainPath);
    // A commit of block 0 that persisted only its first sector.
    var block = new[] { 0, 1000, 2048 }.SelectMany(BitConverter.GetBytes);
    Overwrite(provider, mainPath, main.Concat(block).Concat(new byte[1000]).ToArray());

    using var reopened = SmallBlockFactory(provider, mode).Open();
    Assert.That(MissingKeys(reopened, 60), Is.Empty);
    Assert.That(provider.SimulatePowerLoss().ReadAllBytes(mainPath), Is.EqualTo(main));
  }

  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public async Task TruncatingACommittedBlockIsDurableBeforeNewBlocksFollowIt(WriteAheadLogMode mode)
  {
    var provider = new PowerLossFileStreamProvider();
    using (var tree = Factory(provider, mode).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 9400);
      await tree.SyncWALAsync();
    }
    var mainPath = MainWalPath(provider);
    Overwrite(provider, mainPath + ".tail", new byte[8]);

    using var recovered = Factory(provider, mode).Open();
    Assert.That(
        provider.SimulatePowerLoss().ReadAllBytes(mainPath),
        Is.EqualTo(provider.ReadAllBytes(mainPath)));
  }

  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public async Task DiscardedTailIsNotOverwrittenBeforeItsBlocksAreDurable(WriteAheadLogMode mode)
  {
    const int syncedCount = 60;
    var source = new InMemoryFileStreamProvider();
    byte[] syncedTail;
    using (var tree = SmallBlockFactory(source, mode).OpenOrCreate())
    {
      UpsertKeys(tree, 0, syncedCount);
      await tree.SyncWALAsync();
      syncedTail = source.ReadAllBytes(MainWalPath(source) + ".tail");
      UpsertKeys(tree, syncedCount, 200);
      await tree.SyncWALAsync();
    }

    // The process crashes after writing the blocks and before syncing them. The
    // operating system still has them after a restart, so the tail is discarded.
    var provider = Copy(source, new PowerLossFileStreamProvider());
    var mainPath = MainWalPath(provider);
    var main = provider.ReadAllBytes(mainPath);
    Overwrite(provider, mainPath, main[..1]);
    Overwrite(provider, mainPath + ".tail", syncedTail);
    using (var stream = provider.CreateFileStream(mainPath, FileMode.Open, FileAccess.Write, FileShare.None))
    {
      stream.Seek(0, SeekOrigin.End);
      stream.Write(main, 1, main.Length - 1);
    }
    using (SmallBlockFactory(provider, mode).Open())
    {
    }

    using var reopened = SmallBlockFactory(provider.SimulatePowerLoss(), mode).Open();
    Assert.That(MissingKeys(reopened, syncedCount), Is.Empty);
  }

  [Test]
  public async Task EmptyTailDoesNotProveThatAnUncompressedLastBlockIsSynced()
  {
    const WriteAheadLogMode mode = WriteAheadLogMode.SyncCompressed;
    var provider = new InMemoryFileStreamProvider();
    using (var tree = SmallBlockFactory(provider, mode, CompressionMethod.None).OpenOrCreate())
    {
      UpsertKeys(tree, 0, 200);
      await tree.SyncWALAsync();
    }
    // The background tail writer can replace a discarded tail with an empty one,
    // while the last block still holds a torn sector.
    var mainPath = MainWalPath(provider);
    var main = provider.ReadAllBytes(mainPath);
    const int blockLength = 3 * sizeof(int) + 2048;
    var blockCount = (main.Length - 1) / blockLength;
    Assert.That(blockCount, Is.GreaterThan(1));
    var tornStart = main.Length - main.Length % SectorSize - SectorSize;
    Array.Clear(main, tornStart, main.Length - tornStart);
    Overwrite(provider, mainPath, main);
    Overwrite(provider, mainPath + ".tail",
        BitConverter.GetBytes(blockCount).Concat(new byte[sizeof(int)]).ToArray());

    using var reopened = SmallBlockFactory(provider, mode, CompressionMethod.None).Open();
    Assert.That(MissingKeys(reopened, (blockCount - 1) * 2048 / 28), Is.Empty);
  }

  [TestCase(WriteAheadLogMode.Sync)]
  [TestCase(WriteAheadLogMode.SyncCompressed)]
  [TestCase(WriteAheadLogMode.AsyncCompressed)]
  public void PowerLossDuringTreeCreationLeavesACreatableDirectory(WriteAheadLogMode mode)
  {
    var random = new Random(1);
    var provider = new PowerLossFileStreamProvider();
    var images = new List<PowerLossFileStreamProvider>();
    provider.BeforeDiskSync = _ =>
    {
      lock (images)
        images.Add(provider.SimulateTornPowerLoss(random));
    };
    using (Factory(provider, mode).OpenOrCreate())
      provider.BeforeDiskSync = null;

    Assert.That(images, Is.Not.Empty);
    foreach (var image in images.ToArray())
    {
      using var tree = Factory(image, mode).OpenOrCreate();
      tree.Upsert(0, 1);
    }
  }

  [Test]
  public void TreeCreationStillFailsWhenTheMetaFileExists()
  {
    var provider = new InMemoryFileStreamProvider();
    Overwrite(provider, MetaFilePath, "{}"u8.ToArray());
    Assert.Throws<IOException>(
        () => provider.GetDurableFileWriter().WriteNewFile(MetaFilePath, "{\"a\":1}"u8.ToArray()));
    Assert.That(provider.ReadAllBytes(MetaFilePath), Is.EqualTo("{}"u8.ToArray()));
  }

  // Every power loss point of a workload, with torn sectors

  [TestCase(WriteAheadLogMode.Sync, CompressionMethod.Zstd, 1)]
  [TestCase(WriteAheadLogMode.Sync, CompressionMethod.Zstd, 2)]
  [TestCase(WriteAheadLogMode.SyncCompressed, CompressionMethod.Zstd, 1)]
  [TestCase(WriteAheadLogMode.SyncCompressed, CompressionMethod.Zstd, 2)]
  [TestCase(WriteAheadLogMode.SyncCompressed, CompressionMethod.None, 1)]
  [TestCase(WriteAheadLogMode.AsyncCompressed, CompressionMethod.Zstd, 1)]
  [TestCase(WriteAheadLogMode.AsyncCompressed, CompressionMethod.Zstd, 2)]
  [TestCase(WriteAheadLogMode.AsyncCompressed, CompressionMethod.LZ4, 1)]
  public async Task SyncedRecordsSurviveTornPowerLosses(
      WriteAheadLogMode mode, CompressionMethod compressionMethod, int seed)
  {
    const int count = 1500;
    var random = new Random(seed);
    var failures = new List<string>();
    var checkedImages = 0;

    ZoneTreeFactory<int, int> ImageFactory(IFileStreamProvider provider)
        => SmallBlockFactory(provider, mode, compressionMethod);

    // Background WAL writers take power loss images too.
    PowerLossFileStreamProvider Tear(PowerLossFileStreamProvider provider)
    {
      lock (random)
        return provider.SimulateTornPowerLoss(random);
    }

    static T[] Snapshot<T>(List<T> list)
    {
      lock (list)
        return list.ToArray();
    }

    // Opens an image and also checks the power loss points of its recovery.
    async Task CheckImage(PowerLossFileStreamProvider image, int synced, int depth, string description)
    {
      ++checkedImages;
      var recoveryImages = new List<PowerLossFileStreamProvider>();
      if (depth > 0)
        image.BeforeDiskSync = _ =>
        {
          var recoveryImage = Tear(image);
          lock (recoveryImages)
            recoveryImages.Add(recoveryImage);
        };
      try
      {
        using var tree = ImageFactory(image).Open();
        var missing = MissingKeys(tree, synced);
        if (missing.Length > 0)
          failures.Add($"{description}: {missing.Length} of {synced} synced keys are missing.");
        if (depth == 0)
          return;
        // The recovered tree must accept writes, and they must survive too.
        UpsertKeys(tree, count, count + 100);
        await tree.SyncWALAsync();
        image.BeforeDiskSync = null;
        using var reopened = ImageFactory(Tear(image)).Open();
        if (MissingKeys(reopened, synced).Length > 0 ||
            Enumerable.Range(count, 100).Any(key => !reopened.TryGet(key, out var value) || value != key + 1))
          failures.Add($"{description}: a write is lost after recovery.");
      }
      catch (Exception e)
      {
        failures.Add($"{description}: {e.GetType().Name}: {e.Message}");
      }
      finally
      {
        image.BeforeDiskSync = null;
      }
      foreach (var recoveryImage in Snapshot(recoveryImages))
        await CheckImage(recoveryImage, synced, depth - 1, description + " > recovery");
    }

    var workload = new PowerLossFileStreamProvider();
    var images = new List<(PowerLossFileStreamProvider Image, int Synced, string File)>();
    var syncedCount = 0;
    using (var tree = ImageFactory(workload).OpenOrCreate())
    {
      workload.BeforeDiskSync = path =>
      {
        // Read the synced count first: the image holds at least those records.
        var synced = Volatile.Read(ref syncedCount);
        var image = Tear(workload);
        lock (images)
          images.Add((image, synced, path));
      };
      for (var key = 0; key < count; ++key)
      {
        tree.Upsert(key, key + 1);
        if (key % 37 == 36)
        {
          await tree.SyncWALAsync();
          Volatile.Write(ref syncedCount, key + 1);
        }
        if (key % 400 == 399)
          tree.Maintenance.MoveMutableSegmentForward();
        if (key % 900 == 899)
          tree.Maintenance.StartMergeOperation()?.Join();
        if (key % 500 == 499)
          tree.Maintenance.SaveMetaData();
      }
      workload.BeforeDiskSync = null;
    }

    var workloadImages = Snapshot(images);
    Assert.That(workloadImages.Length, Is.GreaterThan(40));
    foreach (var (image, synced, file) in workloadImages)
      await CheckImage(image, synced, 1, $"power loss in the sync of {file}, {synced} keys synced");

    TestContext.Out.WriteLine($"Checked {checkedImages} torn images.");
    Assert.That(failures, Is.Empty, $"{failures.Count} of {checkedImages} images failed.");
  }
}
