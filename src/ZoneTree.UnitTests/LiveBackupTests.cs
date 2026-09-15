using System.Text.Json;
using ZoneTree.Backup;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Segments;
using ZoneTree.Segments.Disk;

namespace ZoneTree.UnitTests;

public sealed class LiveBackupTests
{
  [Test]
  public void LiveBackupStartDoesNotCreateGeneration()
  {
    var dataPath = "data/LiveBackupStartDoesNotCreateGeneration";
    DeleteDirectory(dataPath);
    var provider = new InMemoryLiveBackupProvider();

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
    {
      Store = provider,
      BackupAfterMerge = false,
      IncludeInMemoryRecords = false
    });

    backup.Start();

    Assert.That(backup.CurrentGenerationId, Is.EqualTo(0));
    Assert.That(provider.BeginGenerationCount, Is.EqualTo(0));

    backup.Stop();
    backup.WaitForLiveBackup();
    zoneTree.Maintenance.Drop();
  }

  [Test]
  public void LiveBackupStreamsInMemoryRecordsWithoutMovingMutableSegment()
  {
    var dataPath = "data/LiveBackupStreamsInMemoryRecordsWithoutMovingMutableSegment";
    var backupPath = "data/LiveBackupStreamsInMemoryRecordsWithoutMovingMutableSegment.Backup";
    DeleteDirectory(dataPath);
    DeleteDirectory(backupPath);

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    for (var i = 0; i < 10; ++i)
      zoneTree.Upsert(i, i);

    var mutableSegmentId = zoneTree.Maintenance.MutableSegment.SegmentId;
    using var backup = zoneTree.CreateLiveBackup(backupPath);

    backup.Start();
    backup.CreateGeneration();

    var generationId = backup.CurrentGenerationId;
    var catalog = ReadCurrentLocalGenerationCatalog(backupPath);
    var recordBatch = catalog.RecordBatch;
    Assert.That(zoneTree.Maintenance.MutableSegment.SegmentId, Is.EqualTo(mutableSegmentId));
    Assert.That(catalog.GenerationId, Is.EqualTo(generationId));
    Assert.That(recordBatch, Is.Not.Null);
    Assert.That(recordBatch.Completed, Is.True);
    Assert.That(recordBatch.RecordCount, Is.EqualTo(10));
    Assert.That(recordBatch.CompressionMethod, Is.EqualTo(CompressionMethod.Zstd));
    Assert.That(recordBatch.UncompressedLength, Is.GreaterThan(0));
    Assert.That(recordBatch.StoredLength, Is.GreaterThan(0));
    Assert.That(
        File.Exists(Path.Combine(
            backupPath,
            recordBatch.BackupPath.Replace('/', Path.DirectorySeparatorChar))),
        Is.True);

    backup.Stop();
    zoneTree.Maintenance.Drop();
    DeleteDirectory(backupPath);
  }

  [Test]
  public void LiveBackupCopiesDiskSegmentCreatedByMerge()
  {
    var dataPath = "data/LiveBackupCopiesDiskSegmentCreatedByMerge";
    var backupPath = "data/LiveBackupCopiesDiskSegmentCreatedByMerge.Backup";
    DeleteDirectory(dataPath);
    DeleteDirectory(backupPath);

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(3)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    using var backup = zoneTree.CreateLiveBackup(backupPath);
    backup.Start();

    for (var i = 0; i < 10; ++i)
      zoneTree.Upsert(i, i);

    zoneTree.Maintenance.MoveMutableSegmentForward();
    zoneTree.Maintenance.StartMergeOperation().Join();
    backup.Stop();
    backup.WaitForLiveBackup();

    var diskSegment = zoneTree.Maintenance.DiskSegment;
    var diskSegmentId = diskSegment.SegmentId;
    var generationId = backup.CurrentGenerationId;
    var catalog = ReadCurrentLocalGenerationCatalog(backupPath);
    Assert.That(diskSegmentId, Is.GreaterThan(0));
    Assert.That(catalog.GenerationId, Is.EqualTo(generationId));
    Assert.That(catalog.SegmentIds, Does.Contain(diskSegmentId));
    AssertDiskSegmentBackedUp(backupPath, catalog, diskSegment);

    backup.Stop();
    zoneTree.Maintenance.Drop();
    DeleteDirectory(backupPath);
  }

  [Test]
  public async Task LocalLiveBackupCanRestoreDiskSegmentsAndInMemoryRecords()
  {
    var dataPath = "data/LocalLiveBackupCanRestoreDiskSegmentsAndInMemoryRecords";
    var backupPath = "data/LocalLiveBackupCanRestoreDiskSegmentsAndInMemoryRecords.Backup";
    var restorePath = "data/LocalLiveBackupCanRestoreDiskSegmentsAndInMemoryRecords.Restore";
    DeleteDirectory(dataPath);
    DeleteDirectory(backupPath);
    DeleteDirectory(restorePath);

    using (var zoneTree = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(3)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate())
    {
      for (var i = 0; i < 10; ++i)
        zoneTree.Upsert(i, i);

      zoneTree.Maintenance.MoveMutableSegmentForward();
      zoneTree.Maintenance.StartMergeOperation().Join();

      for (var i = 10; i < 15; ++i)
        zoneTree.Upsert(i, i);

      using var backup = zoneTree.CreateLiveBackup(backupPath);
      backup.Start();
      backup.CreateGeneration();
      backup.Stop();

      zoneTree.Maintenance.Drop();
    }

    using (var restored = await new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetDataDirectory(restorePath)
        .SetWriteAheadLogDirectory(restorePath)
        .RestoreFromLatestLiveBackup(backupPath))
    {
      for (var i = 0; i < 15; ++i)
      {
        Assert.That(restored.TryGet(i, out var value), Is.True);
        Assert.That(value, Is.EqualTo(i));
      }

      restored.Maintenance.Drop();
    }

    DeleteDirectory(backupPath);
  }

  [Test]
  public async Task LocalLiveBackupRestorePreservesDeletedInMemoryRecords()
  {
    var dataPath = "data/LocalLiveBackupRestorePreservesDeletedInMemoryRecords";
    var backupPath = "data/LocalLiveBackupRestorePreservesDeletedInMemoryRecords.Backup";
    var restorePath = "data/LocalLiveBackupRestorePreservesDeletedInMemoryRecords.Restore";
    DeleteDirectory(dataPath);
    DeleteDirectory(backupPath);
    DeleteDirectory(restorePath);

    using (var zoneTree = new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate())
    {
      zoneTree.Upsert(1, 1);
      zoneTree.ForceDelete(1);
      zoneTree.Upsert(2, 2);

      using var backup = zoneTree.CreateLiveBackup(backupPath);
      backup.Start();
      backup.CreateGeneration();
      backup.Stop();

      zoneTree.Maintenance.Drop();
    }

    using (var restored = await new ZoneTreeFactory<int, int>()
        .SetDataDirectory(restorePath)
        .SetWriteAheadLogDirectory(restorePath)
        .RestoreFromLatestLiveBackup(backupPath))
    {
      Assert.That(restored.TryGet(1, out _), Is.False);
      Assert.That(restored.TryGet(2, out var value), Is.True);
      Assert.That(value, Is.EqualTo(2));

      restored.Maintenance.Drop();
    }

    DeleteDirectory(backupPath);
  }

  [Test]
  public async Task LocalLiveBackupCanRestoreSpecificGeneration()
  {
    var dataPath = "data/LocalLiveBackupCanRestoreSpecificGeneration";
    var backupPath = "data/LocalLiveBackupCanRestoreSpecificGeneration.Backup";
    var restoreFirstPath = "data/LocalLiveBackupCanRestoreSpecificGeneration.First";
    var restoreLatestPath = "data/LocalLiveBackupCanRestoreSpecificGeneration.Latest";
    DeleteDirectory(dataPath);
    DeleteDirectory(backupPath);
    DeleteDirectory(restoreFirstPath);
    DeleteDirectory(restoreLatestPath);

    long firstGenerationId;
    using (var zoneTree = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate())
    {
      for (var i = 0; i < 5; ++i)
        zoneTree.Upsert(i, i);

      using var backup = zoneTree.CreateLiveBackup(backupPath);
      await backup.CreateGenerationAsync();
      firstGenerationId = backup.CurrentGenerationId;

      for (var i = 5; i < 10; ++i)
        zoneTree.Upsert(i, i);

      backup.CreateGeneration();
      backup.Stop();
      backup.WaitForLiveBackup();
      zoneTree.Maintenance.Drop();
    }

    using (var restored = await new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetDataDirectory(restoreFirstPath)
        .SetWriteAheadLogDirectory(restoreFirstPath)
        .RestoreFromLiveBackupGeneration(backupPath, firstGenerationId))
    {
      for (var i = 0; i < 5; ++i)
        Assert.That(restored.TryGet(i, out _), Is.True);
      for (var i = 5; i < 10; ++i)
        Assert.That(restored.TryGet(i, out _), Is.False);
      restored.Maintenance.Drop();
    }

    using (var restored = await new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetDataDirectory(restoreLatestPath)
        .SetWriteAheadLogDirectory(restoreLatestPath)
        .RestoreFromLatestLiveBackup(backupPath))
    {
      for (var i = 0; i < 10; ++i)
        Assert.That(restored.TryGet(i, out _), Is.True);
      restored.Maintenance.Drop();
    }

    DeleteDirectory(backupPath);
  }

  [Test]
  public void LocalLiveBackupRestoreThrowsDedicatedExceptionWhenTargetExists()
  {
    var dataPath = "data/LocalLiveBackupRestoreThrowsDedicatedExceptionWhenTargetExists";
    var backupPath = "data/LocalLiveBackupRestoreThrowsDedicatedExceptionWhenTargetExists.Backup";
    var restorePath = "data/LocalLiveBackupRestoreThrowsDedicatedExceptionWhenTargetExists.Restore";
    DeleteDirectory(dataPath);
    DeleteDirectory(backupPath);
    DeleteDirectory(restorePath);

    using (var zoneTree = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate())
    {
      zoneTree.Upsert(1, 1);
      using var backup = zoneTree.CreateLiveBackup(backupPath);
      backup.Start();
      backup.CreateGeneration();
      backup.Stop();
      zoneTree.Maintenance.Drop();
    }

    using (var existing = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetDataDirectory(restorePath)
        .SetWriteAheadLogDirectory(restorePath)
        .OpenOrCreate())
    {
      existing.Upsert(2, 2);
    }

    Assert.ThrowsAsync<LiveBackupRestoreTargetAlreadyExistsException>(async () =>
      await new ZoneTreeFactory<int, int>()
         .DisableDeletion()
         .SetDataDirectory(restorePath)
         .SetWriteAheadLogDirectory(restorePath)
         .RestoreFromLatestLiveBackup(backupPath)
    );

    using (var existing = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .SetDataDirectory(restorePath)
        .SetWriteAheadLogDirectory(restorePath)
        .Open())
    {
      existing.Maintenance.Drop();
    }

    DeleteDirectory(backupPath);
  }

  [Test]
  public void SnapshotReadOnlyLiveBackupMovesMutableSegmentForward()
  {
    var dataPath = "data/SnapshotReadOnlyLiveBackupMovesMutableSegmentForward";
    var backupPath = "data/SnapshotReadOnlyLiveBackupMovesMutableSegmentForward.Backup";
    DeleteDirectory(dataPath);
    DeleteDirectory(backupPath);

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    for (var i = 0; i < 10; ++i)
      zoneTree.Upsert(i, i);

    var mutableSegmentId = zoneTree.Maintenance.MutableSegment.SegmentId;
    using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
    {
      Store = new LocalLiveBackupProvider(backupPath),
      InMemoryMode = LiveBackupInMemoryMode.Snapshot
    });

    backup.Start();
    backup.CreateGeneration();

    var catalog = ReadCurrentLocalGenerationCatalog(backupPath);
    var recordBatch = catalog.RecordBatch;
    Assert.That(zoneTree.Maintenance.MutableSegment.SegmentId, Is.Not.EqualTo(mutableSegmentId));
    Assert.That(catalog.GenerationId, Is.EqualTo(backup.CurrentGenerationId));
    Assert.That(recordBatch, Is.Not.Null);
    Assert.That(recordBatch.Completed, Is.True);
    Assert.That(recordBatch.RecordCount, Is.EqualTo(10));

    backup.Stop();
    zoneTree.Maintenance.Drop();
    DeleteDirectory(backupPath);
  }

  [Test]
  public void FailedLiveBackupReleasesDiskSegmentPin()
  {
    var dataPath = "data/FailedLiveBackupReleasesDiskSegmentPin";
    DeleteDirectory(dataPath);

    var logger = new NullLogger();
    using var zoneTree = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(3)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .SetLogger(logger)
        .OpenOrCreate();

    for (var i = 0; i < 10; ++i)
      zoneTree.Upsert(i, i);

    zoneTree.Maintenance.MoveMutableSegmentForward();
    zoneTree.Maintenance.StartMergeOperation().Join();

    var diskSegment = zoneTree.Maintenance.DiskSegment;
    Assert.That(diskSegment.SegmentId, Is.GreaterThan(0));

    using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
    {
      Store = new FailingLiveBackupProvider(),
      IncludeInMemoryRecords = false
    });

    backup.Start();
    var exception = Assert.Throws<LiveBackupGenerationException>(
      () => backup.CreateGeneration());
    Assert.That(exception, Is.Not.Null);
    Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());

    var diskSegmentFiles = diskSegment.GetFiles();
    Assert.That(diskSegmentFiles, Is.Not.Empty);

    diskSegment.Drop();

    Assert.That(
        diskSegmentFiles.All(file => !File.Exists(file.Path)),
        Is.True,
        "The failed backup left an iterator lease attached to the disk segment.");

    backup.Stop();
    zoneTree.Maintenance.Drop();
  }

  [Test]
  public void RepeatedLiveBackupGenerationReusesCopiedDiskSegmentFiles()
  {
    var dataPath = "data/RepeatedLiveBackupGenerationReusesCopiedDiskSegmentFiles";
    var backupPath = "data/RepeatedLiveBackupGenerationReusesCopiedDiskSegmentFiles.Backup";
    DeleteDirectory(dataPath);
    DeleteDirectory(backupPath);

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(3)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    for (var i = 0; i < 10; ++i)
      zoneTree.Upsert(i, i);

    zoneTree.Maintenance.MoveMutableSegmentForward();
    zoneTree.Maintenance.StartMergeOperation().Join();

    var diskSegmentId = zoneTree.Maintenance.DiskSegment.SegmentId;
    using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
    {
      Store = new LocalLiveBackupProvider(backupPath),
      IncludeInMemoryRecords = false
    });

    backup.Start();
    backup.CreateGeneration();
    var firstCatalog = ReadCurrentLocalGenerationCatalog(backupPath);
    var firstFileCount = firstCatalog.Files.Count;

    backup.CreateGeneration();

    var latestGenerationId = backup.CurrentGenerationId;
    var secondCatalog = ReadCurrentLocalGenerationCatalog(backupPath);
    Assert.That(latestGenerationId, Is.GreaterThanOrEqualTo(2));
    Assert.That(secondCatalog.SegmentIds, Does.Contain(diskSegmentId));
    Assert.That(secondCatalog.Files.Count, Is.EqualTo(firstFileCount));

    backup.Stop();
    zoneTree.Maintenance.Drop();
    DeleteDirectory(backupPath);
  }

  [Test]
  public void LiveBackupAsksProviderBeforeUploadingExistingSegment()
  {
    var dataPath = "data/LiveBackupAsksProviderBeforeUploadingExistingSegment";
    DeleteDirectory(dataPath);
    var provider = new CatalogTrackingLiveBackupProvider();

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .DisableDeletion()
        .Configure(options => options.AllowUnsafeOptionValues = true)
        .SetMutableSegmentMaxItemCount(3)
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    for (var i = 0; i < 10; ++i)
      zoneTree.Upsert(i, i);

    zoneTree.Maintenance.MoveMutableSegmentForward();
    zoneTree.Maintenance.StartMergeOperation().Join();

    using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
    {
      Store = provider,
      IncludeInMemoryRecords = false
    });

    backup.Start();
    backup.CreateGeneration();
    var firstUploadCount = provider.FileUploadCount;
    Assert.That(firstUploadCount, Is.GreaterThan(0));

    backup.CreateGeneration();

    Assert.That(provider.FileUploadCount, Is.EqualTo(firstUploadCount));
    Assert.That(provider.UseSegmentCallCount, Is.GreaterThanOrEqualTo(2));

    backup.Stop();
    zoneTree.Maintenance.Drop();
  }

  [Test]
  public void LocalLiveBackupRetentionKeepsLastGenerations()
  {
    var dataPath = "data/LocalLiveBackupRetentionKeepsLastGenerations";
    var backupPath = "data/LocalLiveBackupRetentionKeepsLastGenerations.Backup";
    DeleteDirectory(dataPath);
    DeleteDirectory(backupPath);

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    for (var i = 0; i < 10; ++i)
      zoneTree.Upsert(i, i);

    using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
    {
      Store = new LocalLiveBackupProvider(
          new LocalLiveBackupOptions
          {
            Directory = backupPath,
            KeepLastGenerations = 2
          }),
      BackupAfterMerge = false
    });

    backup.Start();
    backup.CreateGeneration();
    backup.CreateGeneration();
    backup.CreateGeneration();

    var manifest = ReadLocalManifest(backupPath);
    Assert.That(manifest.CurrentGenerationId, Is.EqualTo(3));

    var generationsPath = Path.Combine(backupPath, "generations");
    var generationFiles = Directory.GetFiles(generationsPath, "*.json")
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();
    Assert.That(generationFiles.Length, Is.EqualTo(2));
    Assert.That(
        generationFiles.Select(x => Path.GetFileNameWithoutExtension(x)),
        Is.EqualTo(new[]
        {
          2L.ToString("D20"),
          3L.ToString("D20")
        }));

    var recordsPath = Path.Combine(backupPath, "records");
    var recordFiles = Directory.GetFiles(recordsPath, "*.bin");
    Assert.That(recordFiles.Length, Is.EqualTo(2));
    foreach (var generationId in new[] { 2L, 3L })
    {
      var catalog = ReadLocalGenerationCatalog(backupPath, generationId);
      Assert.That(
          File.Exists(Path.Combine(
              backupPath,
              catalog.RecordBatch.BackupPath.Replace('/', Path.DirectorySeparatorChar))),
          Is.True);
    }

    backup.Stop();
    backup.WaitForLiveBackup();
    zoneTree.Maintenance.Drop();
    DeleteDirectory(backupPath);
  }

  [Test]
  public void LocalLiveBackupReadsLegacyJsonMetadata()
  {
    var backupPath = "data/LocalLiveBackupReadsLegacyJsonMetadata.Backup";
    DeleteDirectory(backupPath);
    try
    {
      var generationsPath = Path.Combine(backupPath, "generations");
      Directory.CreateDirectory(generationsPath);
      File.WriteAllText(
          Path.Combine(backupPath, "manifest.json"),
          """
          {
            "Version": 1,
            "CreatedAtUtc": "2024-01-02T03:04:05.0000000Z",
            "UpdatedAtUtc": "2024-01-02T03:05:06.0000000Z",
            "CurrentGenerationId": 7
          }
          """);
      File.WriteAllText(
          Path.Combine(generationsPath, "00000000000000000007.json"),
          """
          {
            "GenerationId": 7,
            "LastOpIndex": 42,
            "StartedAtUtc": "2024-01-02T03:04:10.0000000Z",
            "SegmentIds": [101, 102],
            "Files": [
              {
                "SegmentId": 101,
                "Order": 0,
                "FileName": "segment-101.dat",
                "BackupPath": "data/segment-101.dat",
                "RecordCount": 12,
                "ByteLength": 3456,
                "CopiedAtUtc": "2024-01-02T03:04:11.0000000Z"
              }
            ],
            "RecordBatch": {
              "BatchId": 9,
              "BackupPath": "records/00000000000000000009.bin",
              "RecordCount": 2,
              "CompressionMethod": 1,
              "CompressionLevel": 3,
              "CompressionBlockSize": 4096,
              "UncompressedLength": 100,
              "StoredLength": 80,
              "StartedAtUtc": "2024-01-02T03:04:12.0000000Z",
              "CompletedAtUtc": "2024-01-02T03:04:13.0000000Z",
              "Completed": true
            }
          }
          """);

      var provider = new LocalLiveBackupProvider(backupPath);
      var catalog = provider.ReadCurrentGeneration();

      Assert.That(catalog.GenerationId, Is.EqualTo(7));
      Assert.That(catalog.LastOpIndex, Is.EqualTo(42));
      Assert.That(catalog.SegmentIds, Is.EqualTo(new[] { 101L, 102L }));
      Assert.That(catalog.Files, Has.Count.EqualTo(1));
      Assert.That(catalog.Files[0].BackupPath, Is.EqualTo("data/segment-101.dat"));
      Assert.That(catalog.RecordBatch, Is.Not.Null);
      Assert.That(catalog.RecordBatch.CompressionMethod, Is.EqualTo(CompressionMethod.Zstd));
      Assert.That(catalog.RecordBatch.Completed, Is.True);
      Assert.That(provider.ReadGeneration(7).RecordBatch.StoredLength, Is.EqualTo(80));
    }
    finally
    {
      DeleteDirectory(backupPath);
    }
  }

  [Test]
  public void LiveBackupEveryScheduleCreatesAdditionalGenerations()
  {
    var dataPath = "data/LiveBackupEveryScheduleCreatesAdditionalGenerations";
    var backupPath = "data/LiveBackupEveryScheduleCreatesAdditionalGenerations.Backup";
    DeleteDirectory(dataPath);
    DeleteDirectory(backupPath);

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
    {
      Store = new LocalLiveBackupProvider(backupPath),
      BackupAfterMerge = false,
      IncludeInMemoryRecords = false,
      Schedule = LiveBackupSchedule.Every(TimeSpan.FromMilliseconds(25))
    });

    backup.Start();
    var generated = SpinWait.SpinUntil(() =>
    {
      return backup.CurrentGenerationId >= 2;
    }, TimeSpan.FromSeconds(3));

    backup.Stop();
    backup.WaitForLiveBackup();
    zoneTree.Maintenance.Drop();
    DeleteDirectory(backupPath);

    Assert.That(generated, Is.True);
  }

  [Test]
  public void LiveBackupScheduleStopsCleanly()
  {
    var dataPath = "data/LiveBackupScheduleStopsCleanly";
    DeleteDirectory(dataPath);

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
    {
      Store = new InMemoryLiveBackupProvider(),
      BackupAfterMerge = false,
      IncludeInMemoryRecords = false,
      Schedule = LiveBackupSchedule.Every(TimeSpan.FromMilliseconds(25))
    });

    backup.Start();
    var generated = SpinWait.SpinUntil(() =>
    {
      return backup.CurrentGenerationId >= 2;
    }, TimeSpan.FromSeconds(3));
    Assert.That(generated, Is.True);

    backup.Stop();
    var generationId = backup.CurrentGenerationId;

    Assert.That(backup.CurrentGenerationId, Is.EqualTo(generationId));
    backup.WaitForLiveBackup();
    zoneTree.Maintenance.Drop();
  }

  [Test]
  public void LiveBackupScheduleDoesNotQueueBacklogWhenGenerationIsBusy()
  {
    var dataPath = "data/LiveBackupScheduleDoesNotQueueBacklogWhenGenerationIsBusy";
    DeleteDirectory(dataPath);
    var provider = new SlowCompleteLiveBackupProvider(TimeSpan.FromMilliseconds(150));

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
    {
      Store = provider,
      BackupAfterMerge = false,
      IncludeInMemoryRecords = false,
      Schedule = LiveBackupSchedule.Every(TimeSpan.FromMilliseconds(10))
    });

    backup.Start();
    Thread.Sleep(80);
    var generationIdWhileBusy = backup.CurrentGenerationId;
    Assert.That(generationIdWhileBusy, Is.LessThanOrEqualTo(1));

    backup.Stop();
    zoneTree.Maintenance.Drop();
  }

  [Test]
  public void LiveBackupSerializesConcurrentGenerationCompletion()
  {
    var dataPath = "data/LiveBackupSerializesConcurrentGenerationCompletion";
    DeleteDirectory(dataPath);
    var provider = new ConcurrentCompletionTrackingLiveBackupProvider(
        TimeSpan.FromMilliseconds(50));

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
    {
      Store = provider,
      BackupAfterMerge = false,
      IncludeInMemoryRecords = false,
      Schedule = LiveBackupSchedule.Every(TimeSpan.FromMilliseconds(10))
    });

    backup.Start();
    var written = SpinWait.SpinUntil(() =>
    {
      return provider.CompleteGenerationCount >= 3;
    }, TimeSpan.FromSeconds(3));

    backup.Stop();
    backup.WaitForLiveBackup();
    zoneTree.Maintenance.Drop();

    Assert.That(written, Is.True);
    Assert.That(provider.MaxConcurrentCompleteGenerations, Is.EqualTo(1));
  }

  [Test]
  public void LiveBackupScheduleValidationRejectsInvalidSchedules()
  {
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        LiveBackupSchedule.Every(TimeSpan.Zero));
    Assert.Throws<ArgumentException>(() =>
        LiveBackupSchedule.Daily());
    Assert.Throws<ArgumentException>(() =>
        LiveBackupSchedule.Weekly());

    var daily = LiveBackupSchedule.Daily(
        new TimeOnly(2, 0),
        new TimeOnly(2, 0),
        new TimeOnly(14, 0));
    var weekly = LiveBackupSchedule.Weekly(
        LiveBackupSchedule.On(DayOfWeek.Sunday, new TimeOnly(2, 0)),
        LiveBackupSchedule.On(DayOfWeek.Sunday, new TimeOnly(2, 0)));

    Assert.That(daily.DailyUtcTimes.Length, Is.EqualTo(2));
    Assert.That(weekly.WeeklyUtcTimes.Length, Is.EqualTo(1));
  }

  [Test]
  public void LiveBackupOptionsNormalizeResetsInvalidFileTransferCount()
  {
    var options = new LiveBackupOptions
    {
      MaxConcurrentFileTransfers = 0
    };

    options.Normalize();

    Assert.That(options.MaxConcurrentFileTransfers, Is.EqualTo(8));
  }

  [Test]
  public void LiveBackupCanRestartAfterStop()
  {
    var dataPath = "data/LiveBackupCanRestartAfterStop";
    DeleteDirectory(dataPath);

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
    {
      Store = new InMemoryLiveBackupProvider(),
      BackupAfterMerge = false,
      IncludeInMemoryRecords = false
    });

    backup.Start();
    backup.CreateGeneration();
    backup.Stop();
    var firstGenerationId = backup.CurrentGenerationId;

    backup.Start();
    backup.CreateGeneration();
    backup.Stop();

    Assert.That(backup.CurrentGenerationId, Is.GreaterThan(firstGenerationId));

    zoneTree.Maintenance.Drop();
  }

  [Test]
  public void LiveBackupCannotRestartAfterDispose()
  {
    var dataPath = "data/LiveBackupCannotRestartAfterDispose";
    DeleteDirectory(dataPath);

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .OpenOrCreate();

    var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
    {
      Store = new InMemoryLiveBackupProvider(),
      BackupAfterMerge = false,
      IncludeInMemoryRecords = false
    });

    backup.Dispose();

    Assert.Throws<ObjectDisposedException>(() =>
        backup.Start());
    Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        await backup.CreateGenerationAsync());

    zoneTree.Maintenance.Drop();
  }

  [Test]
  public void ScheduledLiveBackupFailureIsReportedByWait()
  {
    var dataPath = "data/ScheduledLiveBackupFailureIsReportedByWait";
    DeleteDirectory(dataPath);
    var provider = new FailingScheduledCompleteLiveBackupProvider();
    var logger = new NullLogger();

    using var zoneTree = new ZoneTreeFactory<int, int>()
        .SetDataDirectory(dataPath)
        .SetWriteAheadLogDirectory(dataPath)
        .SetLogger(logger)
        .OpenOrCreate();

    using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
    {
      Store = provider,
      BackupAfterMerge = false,
      IncludeInMemoryRecords = false,
      Schedule = LiveBackupSchedule.Every(TimeSpan.FromMilliseconds(25))
    });

    backup.Start();
    var scheduledGenerationStarted = SpinWait.SpinUntil(() =>
        provider.BeginGenerationCount > 3,
        TimeSpan.FromSeconds(3));

    Assert.That(scheduledGenerationStarted, Is.True);
    Assert.That(logger.Errors.Any(x => x is LiveBackupGenerationException), Is.True);

    backup.Stop();
    backup.WaitForLiveBackup();
    zoneTree.Maintenance.Drop();
  }

  static void DeleteDirectory(string path)
  {
    if (Directory.Exists(path))
      Directory.Delete(path, true);
  }

  static TestLocalDirectoryManifest ReadLocalManifest(string backupPath)
  {
    return JsonSerializer.Deserialize<TestLocalDirectoryManifest>(
        File.ReadAllText(Path.Combine(backupPath, "manifest.json")));
  }

  static TestLiveBackupGenerationCatalog ReadCurrentLocalGenerationCatalog(
      string backupPath)
  {
    var manifest = ReadLocalManifest(backupPath);
    return ReadLocalGenerationCatalog(
        backupPath,
        manifest.CurrentGenerationId);
  }

  static TestLiveBackupGenerationCatalog ReadLocalGenerationCatalog(
      string backupPath,
      long generationId)
  {
    var path = Path.Combine(
        backupPath,
        "generations",
        generationId.ToString("D20") + ".json");
    return JsonSerializer.Deserialize<TestLiveBackupGenerationCatalog>(
        File.ReadAllText(path));
  }

  static void AssertDiskSegmentBackedUp(
      string backupPath,
      TestLiveBackupGenerationCatalog catalog,
      IDiskSegment<int, int> diskSegment)
  {
    var partCount = diskSegment.GetPartCount();
    if (partCount == 0)
    {
      AssertBackupFileExists(
          backupPath,
          catalog,
          diskSegment);
      return;
    }

    AssertBackupFileExists(
        backupPath,
        catalog,
        diskSegment);
    for (var i = 0; i < partCount; ++i)
      AssertDiskSegmentBackedUp(backupPath, catalog, diskSegment.GetPart(i));
  }

  static void AssertBackupFileExists(
      string backupPath,
      TestLiveBackupGenerationCatalog catalog,
      IDiskSegment<int, int> diskSegment)
  {
    var file = catalog.Files.FirstOrDefault(x =>
        x.SegmentId == diskSegment.SegmentId);
    Assert.That(file, Is.Not.Null);
    Assert.That(file.RecordCount, Is.EqualTo(diskSegment.Length));
    Assert.That(file.ByteLength, Is.GreaterThan(0));
    Assert.That(
        File.Exists(Path.Combine(
            backupPath,
            file.BackupPath.Replace('/', Path.DirectorySeparatorChar))),
        Is.True);
  }

  public sealed class TestLiveBackupGenerationCatalog
  {
    public long GenerationId { get; set; }

    public long[] SegmentIds { get; set; } = [];

    public List<TestLocalLiveBackupFile> Files { get; set; } = [];

    public TestLocalLiveBackupRecordBatch RecordBatch { get; set; }
  }

  public sealed class TestLocalDirectoryManifest
  {
    public long CurrentGenerationId { get; set; }
  }

  public sealed class TestLocalLiveBackupFile
  {
    public long SegmentId { get; set; }

    public string FileName { get; set; }

    public string BackupPath { get; set; }

    public long RecordCount { get; set; }

    public long ByteLength { get; set; }

    public string CopiedAtUtc { get; set; }
  }

  public sealed class TestLocalLiveBackupRecordBatch
  {
    public long BatchId { get; set; }

    public string BackupPath { get; set; }

    public long RecordCount { get; set; }

    public CompressionMethod CompressionMethod { get; set; }

    public int CompressionLevel { get; set; }

    public int CompressionBlockSize { get; set; }

    public long UncompressedLength { get; set; }

    public long StoredLength { get; set; }

    public string StartedAtUtc { get; set; }

    public string CompletedAtUtc { get; set; }

    public bool Completed { get; set; }
  }

  sealed class FailingLiveBackupProvider : InMemoryLiveBackupProvider
  {
    public override Task UploadSegmentFileAsync(
        long generationId,
        DiskSegmentFile file,
        Stream source,
        CancellationToken cancellationToken)
    {
      throw new InvalidOperationException("Backup file copy failed.");
    }
  }

  sealed class NullLogger : ILogger
  {
    readonly List<Exception> ErrorList = [];

    public LogLevel LogLevel { get; set; }

    public Exception[] Errors => [.. ErrorList];

    public void LogError(Exception log)
    {
      ErrorList.Add(log);
    }

    public void LogWarning(object log)
    {
    }

    public void LogInfo(object log)
    {
    }

    public void LogTrace(object log)
    {
    }
  }

  sealed class NullLiveBackupRecordWriter : ILiveBackupRecordWriter
  {
    public Task WriteAsync(
        LiveBackupRecord record,
        CancellationToken cancellationToken)
    {
      return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
      return ValueTask.CompletedTask;
    }
  }

  class InMemoryLiveBackupProvider : ILiveBackupStore
  {
    readonly List<long> Generations = [];

    readonly List<DiskSegmentFile> Files = [];

    readonly List<LiveBackupRecordBatch> RecordBatches = [];

    int BeginGenerations;

    long LastGenerationId;

    public int GenerationCount => Generations.Count;

    public int BeginGenerationCount => Volatile.Read(ref BeginGenerations);

    public long LastGenerationIdValue => Generations.LastOrDefault();

    public DiskSegmentFile[] UploadedFiles => [.. Files];

    public LiveBackupRecordBatch[] UploadedRecordBatches => [.. RecordBatches];

    public virtual Task<long> GetNextGenerationIdAsync(
        CancellationToken cancellationToken)
    {
      return Task.FromResult(Interlocked.Increment(ref LastGenerationId));
    }

    public virtual Task BeginGenerationAsync(
        long generationId,
        DateTime startedAtUtc,
        CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref BeginGenerations);
      return Task.CompletedTask;
    }

    public virtual Task<bool> UseSegmentAsync(
        long generationId,
        DiskSegmentFile file,
        CancellationToken cancellationToken)
    {
      return Task.FromResult(true);
    }

    public virtual Task UploadSegmentFileAsync(
        long generationId,
        DiskSegmentFile file,
        Stream source,
        CancellationToken cancellationToken)
    {
      Files.Add(file);
      return Task.CompletedTask;
    }

    public virtual Task<ILiveBackupRecordWriter> OpenRecordWriterAsync(
        long generationId,
        LiveBackupRecordBatch batch,
        CancellationToken cancellationToken)
    {
      RecordBatches.Add(batch);
      return Task.FromResult<ILiveBackupRecordWriter>(
          new NullLiveBackupRecordWriter());
    }

    public virtual Task CompleteGenerationAsync(
        long generationId, long lastOpIndex,
        CancellationToken cancellationToken)
    {
      Generations.Add(generationId);
      return Task.CompletedTask;
    }
  }

  sealed class SlowCompleteLiveBackupProvider(TimeSpan delay) : InMemoryLiveBackupProvider
  {
    public override async Task CompleteGenerationAsync(
        long generationId, long lastOpIndex,
        CancellationToken cancellationToken)
    {
      await Task.Delay(delay, cancellationToken);
      await base.CompleteGenerationAsync(generationId, lastOpIndex, cancellationToken);
    }
  }

  sealed class ConcurrentCompletionTrackingLiveBackupProvider(TimeSpan delay)
            : InMemoryLiveBackupProvider
  {
    readonly TimeSpan Delay = delay;

    int CurrentCompleteGenerations;

    int CompleteGenerations;

    int MaxConcurrentWrites;

    public int CompleteGenerationCount => Volatile.Read(ref CompleteGenerations);

    public int MaxConcurrentCompleteGenerations => Volatile.Read(ref MaxConcurrentWrites);

    public override async Task CompleteGenerationAsync(
        long generationId, long lastOpIndex,
        CancellationToken cancellationToken)
    {
      var current = Interlocked.Increment(ref CurrentCompleteGenerations);
      Interlocked.Increment(ref CompleteGenerations);
      UpdateMaxConcurrentWrites(current);
      try
      {
        await Task.Delay(Delay, cancellationToken);
        await base.CompleteGenerationAsync(generationId, lastOpIndex, cancellationToken);
      }
      finally
      {
        Interlocked.Decrement(ref CurrentCompleteGenerations);
      }
    }

    void UpdateMaxConcurrentWrites(int current)
    {
      while (true)
      {
        var observed = Volatile.Read(ref MaxConcurrentWrites);
        if (current <= observed)
          return;
        if (Interlocked.CompareExchange(
            ref MaxConcurrentWrites,
            current,
            observed) == observed)
        {
          return;
        }
      }
    }
  }

  sealed class FailingScheduledCompleteLiveBackupProvider
      : InMemoryLiveBackupProvider
  {
    public override Task CompleteGenerationAsync(
        long generationId, long lastOpIndex,
        CancellationToken cancellationToken)
    {
      if (generationId >= 2)
        throw new InvalidOperationException("Scheduled backup failed.");
      return base.CompleteGenerationAsync(generationId, lastOpIndex, cancellationToken);
    }
  }

  sealed class CatalogTrackingLiveBackupProvider
      : InMemoryLiveBackupProvider
  {
    readonly Lock SyncRoot = new();

    readonly HashSet<string> Files = new(StringComparer.Ordinal);

    int FileUploads;

    int UseSegmentCalls;

    public int FileUploadCount => Volatile.Read(ref FileUploads);

    public int UseSegmentCallCount => Volatile.Read(ref UseSegmentCalls);

    public override Task UploadSegmentFileAsync(
        long generationId,
        DiskSegmentFile file,
        Stream source,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      ArgumentNullException.ThrowIfNull(file);
      Interlocked.Increment(ref FileUploads);
      lock (SyncRoot)
        Files.Add(file.FileName);
      return Task.CompletedTask;
    }

    public override Task<bool> UseSegmentAsync(
        long generationId,
        DiskSegmentFile file,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      ArgumentNullException.ThrowIfNull(file);
      Interlocked.Increment(ref UseSegmentCalls);
      lock (SyncRoot)
        return Task.FromResult(!Files.Contains(file.FileName));
    }
  }

  [TestCase(true)]
  [TestCase(false)]
  public async Task LiveBackupIncrementalBackup(bool single)
  {
    for (var k = 0; k < 1; k++)
    {
      var restorePath = "data/LiveBackupIncrementalBackup.Restore";
      var dataPath = "data/LiveBackupIncrementalBackup";
      var backupPath = "data/LiveBackupIncrementalBackup.Backup";
      DeleteDirectory(restorePath);
      DeleteDirectory(dataPath);
      DeleteDirectory(backupPath);

      using var zoneTree = new ZoneTreeFactory<int, int>()
          .DisableDeletion()
          .Configure(options => options.AllowUnsafeOptionValues = true)
          .ConfigureDiskSegmentOptions(x =>
          {
            x.DiskSegmentMode = single ? DiskSegmentMode.SingleDiskSegment : DiskSegmentMode.MultiPartDiskSegment;
            x.MinimumRecordCount = 10;
            x.MaximumRecordCount = 20;
          })
          .SetDataDirectory(dataPath)
          .OpenOrCreate();

      using var backup = zoneTree.CreateLiveBackup(new LocalLiveBackupOptions
      {
        Directory = backupPath,
        KeepLastGenerations = 222,
      });

      for (var i = 0; i < 100; ++i)
        zoneTree.Upsert(i, i * 2);

      zoneTree.Maintenance.MoveMutableSegmentForward();
      zoneTree.Maintenance.StartMergeOperation().Join();
      await backup.CreateGenerationAsync();
      await backup.CreateGenerationAsync();
      await backup.CreateGenerationAsync();
      await backup.CreateGenerationAsync();

      using var restored = await new ZoneTreeFactory<int, int>()
          .DisableDeletion()
          .Configure(options => options.AllowUnsafeOptionValues = true)
          .ConfigureDiskSegmentOptions(x =>
          {
            x.MinimumRecordCount = 10;
            x.MaximumRecordCount = 20;
          })
          .SetDataDirectory(restorePath).RestoreFromLatestLiveBackup(backupPath);

      for (var i = 0; i < 100; ++i)
      {
        var hasKey = restored.TryGet(i, out var j);
        Assert.That(hasKey, Is.True);
        Assert.That(j, Is.EqualTo(i * 2));
      }
      restored.Maintenance.Drop();

      for (var i = 100; i < 200; ++i)
        zoneTree.Upsert(i, i * 2);

      zoneTree.Maintenance.MoveMutableSegmentForward();
      zoneTree.Maintenance.StartMergeOperation().Join();
      for (var i = 200; i < 300; ++i)
        zoneTree.Upsert(i, i * 2);
      await backup.CreateGenerationAsync();
      await backup.CreateGenerationAsync();
      await backup.CreateGenerationAsync();

      using var restored2 = await new ZoneTreeFactory<int, int>()
          .DisableDeletion()
          .Configure(options => options.AllowUnsafeOptionValues = true)
          .ConfigureDiskSegmentOptions(x =>
          {
            x.MinimumRecordCount = 10;
            x.MaximumRecordCount = 20;
          })
          .SetDataDirectory(restorePath).RestoreFromLatestLiveBackup(backupPath);

      for (var i = 0; i < 300; ++i)
      {
        var hasKey = restored2.TryGet(i, out var j);
        Assert.That(hasKey, Is.True);
        Assert.That(j, Is.EqualTo(i * 2));
      }
      zoneTree.Maintenance.SaveMetaData();
      //restored2.Maintenance.Drop();
      zoneTree.Maintenance.Drop();
      DeleteDirectory(backupPath);
    }
  }

  [TestCase(true)]
  [TestCase(false)]
  public async Task LiveBackupBottomSegmentsIncrementalBackup(bool single)
  {
    const int Chunk = 100;
    for (var k = 0; k < 1; k++)
    {
      var restorePath = "data/LiveBackupBottomSegmentsIncrementalBackup.Restore";
      var dataPath = "data/LiveBackupBottomSegmentsIncrementalBackup";
      var backupPath = "data/LiveBackupBottomSegmentsIncrementalBackup.Backup";
      DeleteDirectory(restorePath);
      DeleteDirectory(dataPath);
      DeleteDirectory(backupPath);

      using var zoneTree = new ZoneTreeFactory<int, int>()
          .DisableDeletion()
          .Configure(options => options.AllowUnsafeOptionValues = true)
          .SetDiskSegmentMaxItemCount(50)
          .ConfigureDiskSegmentOptions(x =>
          {
            x.DiskSegmentMode = single ? DiskSegmentMode.SingleDiskSegment : DiskSegmentMode.MultiPartDiskSegment;
            x.MinimumRecordCount = 10;
            x.MaximumRecordCount = 20;
          })
          .SetDataDirectory(dataPath)
          .OpenOrCreate();

      using var backup = zoneTree.CreateLiveBackup(new LocalLiveBackupOptions
      {
        Directory = backupPath,
        KeepLastGenerations = 222,
      });

      for (var i = 0; i < Chunk; ++i)
        zoneTree.Upsert(i, i * 2);

      zoneTree.Maintenance.MoveMutableSegmentForward();
      zoneTree.Maintenance.StartMergeOperation().Join();
      await backup.CreateGenerationAsync();
      await backup.CreateGenerationAsync();
      await backup.CreateGenerationAsync();
      await backup.CreateGenerationAsync();

      using var restored = await new ZoneTreeFactory<int, int>()
          .DisableDeletion()
          .Configure(options => options.AllowUnsafeOptionValues = true)
          .ConfigureDiskSegmentOptions(x =>
          {
            x.MinimumRecordCount = 10;
            x.MaximumRecordCount = 20;
          })
          .SetDataDirectory(restorePath).RestoreFromLatestLiveBackup(backupPath);

      for (var i = 0; i < Chunk; ++i)
      {
        var hasKey = restored.TryGet(i, out var j);
        Assert.That(hasKey, Is.True);
        Assert.That(j, Is.EqualTo(i * 2));
      }
      restored.Maintenance.Drop();

      for (var i = Chunk; i < 2 * Chunk; ++i)
        zoneTree.Upsert(i, i * 2);

      zoneTree.Maintenance.MoveMutableSegmentForward();
      zoneTree.Maintenance.StartMergeOperation().Join();
      for (var i = 2 * Chunk; i < 3 * Chunk; ++i)
        zoneTree.Upsert(i, i * 2);
      await backup.CreateGenerationAsync();
      await backup.CreateGenerationAsync();
      await backup.CreateGenerationAsync();

      using var restored2 = await new ZoneTreeFactory<int, int>()
          .DisableDeletion()
          .Configure(options => options.AllowUnsafeOptionValues = true)
          .ConfigureDiskSegmentOptions(x =>
          {
            x.MinimumRecordCount = 10;
            x.MaximumRecordCount = 20;
          })
          .SetDataDirectory(restorePath).RestoreFromLatestLiveBackup(backupPath);

      for (var i = 0; i < 3 * Chunk; ++i)
      {
        var hasKey = restored2.TryGet(i, out var j);
        Assert.That(hasKey, Is.True);
        Assert.That(j, Is.EqualTo(i * 2));
      }
      zoneTree.Maintenance.SaveMetaData();
      restored2.Maintenance.Drop();
      zoneTree.Maintenance.Drop();
      DeleteDirectory(backupPath);
    }
  }

}
