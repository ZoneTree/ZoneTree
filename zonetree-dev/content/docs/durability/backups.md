# Backups

ZoneTree supports live backup for built-in non-transactional ZoneTree instances and offline full backup for any deployment.

Live backup is the preferred shape for long-running applications. It creates complete backup generations while the tree remains open for reads and writes.

## Live Backup Model

A live backup generation is a complete backup unit. It contains:

* immutable disk segment files,
* optional in-memory records streamed into a record batch,
* a generation catalog written by the backup store.

ZoneTree collects the current backup view, pins the disk segments that belong to that view, and lets the store upload or reuse the segment files. In-memory records are streamed through ZoneTree iterators.

Manual generations run in the caller's operation. Scheduled and merge-triggered generations run asynchronously.

Built-in live backup currently covers non-transactional ZoneTree instances. Transactional backup must capture transaction-log state together with the storage-engine state, so it needs its own design.

## Manual Backup

Use `CreateLiveBackup` with a local directory and create a generation:

```csharp
using ZoneTree.Backup;

using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/app")
    .OpenOrCreate();

using var backup = zoneTree.CreateLiveBackup("backup/app");

await backup.CreateGenerationAsync();
```

Manual generations do not require `Start()`. `CreateGenerationAsync()` completes when the generation is finished. If the generation fails, the exception is propagated to the caller, usually as `LiveBackupGenerationException`.

For non-async callers:

```csharp
backup.CreateGeneration();
```

## Automatic Backup

Call `Start()` when you want live backup to react to normal merges or a UTC schedule.

```csharp
using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
{
    Store = new LocalLiveBackupProvider("backup/app"),
    Schedule = LiveBackupSchedule.Daily(
        new TimeOnly(2, 0),
        new TimeOnly(14, 0))
});

backup.Start();
```

`Start()` enables:

* merge-triggered generations,
* scheduled generations, if a schedule is configured.

`Start()` does not create a generation by itself. A generation is created only when a schedule fires, a successful normal merge requests one, or the application calls `CreateGenerationAsync()`.

By default, successful normal merges request a new generation:

```csharp
BackupAfterMerge = true
```

This is useful because a normal merge can move recent in-memory data into immutable disk segments.

Bottom-segment merges do not request generations. They reshape already durable bottom data and do not move recent mutable data into disk coverage.

Available schedules:

```csharp
LiveBackupSchedule.Every(TimeSpan.FromMinutes(15));

LiveBackupSchedule.Daily(
    new TimeOnly(2, 0),
    new TimeOnly(14, 0));

LiveBackupSchedule.Weekly(
    LiveBackupSchedule.On(DayOfWeek.Sunday, new TimeOnly(2, 0)));
```

Use `Stop()` to disable automatic triggers:

```csharp
backup.Stop();
await backup.WaitForLiveBackupAsync();
```

`Stop()` detaches merge-triggered generation handling and requests scheduler cancellation. It does not wait for the scheduler to exit or for an active generation to finish. `WaitForLiveBackupAsync()` waits until live backup is stopped, the scheduler has exited, and any active generation has completed.

For non-async callers:

```csharp
backup.Stop();
backup.WaitForLiveBackup();
```

`Dispose()` calls `Stop()` and waits for live backup activity to finish.

## Generation Rules

Live backup does not queue generations. ZoneTree allows only one live backup generation at a time.

Manual generation requests are strict. If another generation is already running, `CreateGenerationAsync()` fails instead of queueing another generation.

Scheduled and merge-triggered requests are opportunistic. If another generation is already running, the request is skipped.

```text
manual CreateGenerationAsync() while busy  -> throws
scheduled generation while busy            -> skipped
merge-triggered generation while busy      -> skipped
```

Scheduled and merge-triggered generations run asynchronously. If one starts and then fails, the failure is logged through the ZoneTree logger.

## Shutdown

`Stop()` does not create a final generation. If you want a final backup point before shutdown, create it explicitly:

```csharp
await backup.CreateGenerationAsync();

backup.Stop();
await backup.WaitForLiveBackupAsync();
```

## Local Store

`LocalLiveBackupProvider` writes generations into a local directory.

```csharp
using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
{
    Store = new LocalLiveBackupProvider("backup/app")
});
```

The local implementation stores:

* `manifest.json`: latest completed generation id,
* `generations/*.json`: generation catalogs,
* `data/*`: copied disk segment files,
* `records/*.bin`: in-memory record batches.

Disk segment files are immutable, so the store can reuse files that were already copied by earlier generations.

The local backup directory is shaped like this:

```text
backup/app/
├── manifest.json
├── data/
│   ├── <segment-file>
│   └── ...
├── records/
│   ├── 00000000000000000001.bin
│   └── ...
└── generations/
    ├── 00000000000000000001.json
    └── ...
```

Generation catalogs and record batches use zero-padded numeric names so lexical filename ordering matches numeric generation or batch ordering.

## Restore

Restore uses the read-side backup source.

The local implementation supports both backup and restore, so restoring the latest local generation is direct:

```csharp
using var restored = await new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/restore")
    .RestoreFromLatestLiveBackup("backup/app");
```

To restore a specific generation:

```csharp
using var restored = await new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/restore")
    .RestoreFromLiveBackupGeneration("backup/app", generationId);
```

The restore target must be empty. If the target directory already contains ZoneTree metadata, restore throws `LiveBackupRestoreTargetAlreadyExistsException`.

## Retention

Local retention is configured on `LocalLiveBackupOptions`:

```csharp
using var backup = zoneTree.CreateLiveBackup(new LiveBackupOptions
{
    Store = new LocalLiveBackupProvider(new LocalLiveBackupOptions
    {
        Directory = "backup/app",
        KeepLastGenerations = 7
    })
});
```

The local store keeps the latest completed generations and removes files that are no longer referenced by retained generation catalogs.

Retention is applied at the backup-path level. If a retained generation reuses a segment file that was originally copied by an older generation, the physical segment file is preserved because the retained generation still references the same backup path.

Custom stores own their own retention policy.

## In-Memory Records

By default, live backup includes in-memory records:

```csharp
IncludeInMemoryRecords = true
```

These records are serialized with the ZoneTree key and value serializers. Record batches are compressed by default with Zstd level `0`.

Use `Live` mode when you want the least intrusive backup pass:

```csharp
InMemoryMode = LiveBackupInMemoryMode.Live
```

`Live` mode iterates mutable and read-only in-memory segments without moving the mutable segment forward.

Use `Snapshot` mode when you want a cleaner in-memory boundary:

```csharp
InMemoryMode = LiveBackupInMemoryMode.Snapshot
```

`Snapshot` mode moves the mutable segment forward and then iterates read-only in-memory segments.

If in-memory records are disabled, a generation contains disk segments only. Recent writes become part of later generations after maintenance moves them to disk.

## Custom Stores And Sources

Implement `ILiveBackupStore` when backup data should go somewhere other than a local directory.

Store implementations decide:

* how generation ids are assigned,
* where segment files are stored,
* whether an existing segment file can be reused,
* how record batches are written,
* how retention works.

ZoneTree decides:

* which segments belong to a generation,
* when disk segments must be pinned,
* how in-memory records are streamed,
* when manual, scheduled, and merge-triggered generations are started or skipped.

Segment reuse is handled by `UseSegmentAsync`:

```csharp
Task<bool> UseSegmentAsync(
    long generationId,
    DiskSegmentFile file,
    CancellationToken cancellationToken);
```

Return `false` when the store already has the segment file. Return `true` when ZoneTree should upload the file through `UploadSegmentFileAsync`.

Disk segment files can be uploaded concurrently during a generation. The default is:

```csharp
MaxConcurrentFileTransfers = 8
```

When `MaxConcurrentFileTransfers` is greater than `1`, segment file operations may run concurrently for the same generation. Custom stores should make `UseSegmentAsync` and `UploadSegmentFileAsync` safe for that call pattern, or users should configure `MaxConcurrentFileTransfers = 1`.

Restore uses the matching read-side abstraction:

```csharp
public interface ILiveBackupSource
```

Custom sources must preserve the `Order` value of each `DiskSegmentFile` in the generation they return. Restore uses that order to rebuild the disk segment and bottom segment layout.

## Offline Full Backup

Offline backup is the simple maintenance-window option:

* stop writes,
* wait for maintenance work to finish,
* dispose the tree,
* copy the full ZoneTree data directory,
* copy the WAL directory too if it is configured separately.

A ZoneTree database is a set of files, not a single file. Depending on configuration, storage can include metadata, disk segments, WAL files, meta WAL files, transaction WAL files, and multi-part segment files.

If `SetWriteAheadLogDirectory(...)` points WAL files outside the data directory, copy both locations from the same backup point.

## Rebuildable Data

Some applications use ZoneTree for indexes, projections, caches, or derived views. If the data can be rebuilt from another source of truth, backup can focus on that source instead of the ZoneTree files.

Use no-WAL or relaxed backup policies only when that data-loss boundary is intentional.
