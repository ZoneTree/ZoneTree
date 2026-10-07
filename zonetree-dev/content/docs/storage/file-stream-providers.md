# File Stream Providers

ZoneTree writes physical storage through `IFileStreamProvider`.

This abstraction lets the default ZoneTree storage pipeline open, read, write, replace, and delete files without being tied to one backing store.

The default is `LocalFileStreamProvider`. It stores data on the local file system and is the right choice for normal persistent databases.

## Where It Is Used

The file stream provider is used by the default implementations for:

* write-ahead log files,
* metadata files,
* disk segment files,
* temporary files used during replacement and compaction.

When you create a factory without passing a provider, ZoneTree uses `LocalFileStreamProvider`.

```csharp
using ZoneTree;

using var zoneTree = new ZoneTreeFactory<int, string>()
    .SetDataDirectory("data/app")
    .OpenOrCreate();
```

To use another provider, pass it to the factory constructor.

```csharp
using ZoneTree;
using ZoneTree.AbstractFileStream;

IFileStreamProvider fileStreamProvider = new LocalFileStreamProvider();

using var zoneTree = new ZoneTreeFactory<int, string>(fileStreamProvider)
    .SetDataDirectory("data/app")
    .OpenOrCreate();
```

The factory uses that provider when it creates the default `WriteAheadLogProvider` and `RandomAccessDeviceManager`.

Advanced applications can replace those higher-level components directly with `SetWriteAheadLogProvider` or `SetRandomAccessDeviceManager`.

## Local File Provider

`LocalFileStreamProvider` is the default provider.

Use it for:

* persistent databases,
* crash recovery,
* write-ahead log durability,
* normal production storage,
* live backup and restore.

It delegates to the local file system, so data can survive process exit, machine restart, and application redeploys as long as the underlying storage is available.

## In-Memory File Provider

`InMemoryFileStreamProvider` stores ZoneTree's physical files in process memory.

It still runs ZoneTree's normal stream-based storage flow: metadata, WAL files, disk segment files, temporary files, and merge output are represented as files, but those files live inside the provider instance instead of on disk.

Use it when you want ZoneTree's ordered storage engine behavior and do not need durability.

Good use cases:

* fast tests that should not touch the local file system,
* high-RAM machines running durability-optional jobs,
* temporary indexing, sorting, staging, or deduplication,
* data transfer and transformation pipelines where source data can be replayed,
* cache-like or rebuildable datasets.

```csharp
using ZoneTree;
using ZoneTree.AbstractFileStream;
using ZoneTree.Options;

var provider = new InMemoryFileStreamProvider();

using var zoneTree = new ZoneTreeFactory<long, string>(provider)
    .SetDataDirectory("transfer-job")
    .ConfigureWriteAheadLogOptions(options =>
    {
        options.WriteAheadLogMode = WriteAheadLogMode.None;
    })
    .OpenOrCreate();
```

For normal in-memory-provider workloads, prefer `WriteAheadLogMode.None`.

With `InMemoryFileStreamProvider`, WAL files are also stored in process memory. They do not protect against process termination, machine restart, power loss, or losing the provider instance. Keeping WAL enabled can still be useful in tests that specifically exercise WAL behavior, replay logic, compaction, or recovery code while the provider instance is alive.

Use a durable provider, such as `LocalFileStreamProvider`, when WAL durability matters.

## In-Memory Semantics

`InMemoryFileStreamProvider` is optimized for ZoneTree's own stream usage, not exact file-system emulation.

Important behavior:

* data is scoped to the provider instance,
* all data is lost when that instance is lost,
* writes, truncates, and replacements are visible immediately,
* large files use chunked backing storage,
* small files avoid large chunk allocation,
* `ReadAllBytes` is a small-file convenience API and may throw for very large files,
* `Replace` uses fast in-memory object handoff rather than copying large buffers,
* `FileShare` is intentionally ignored,
* open streams can keep referencing an old in-memory file object after delete or replace,
* per-stream `Position` is not thread-safe.

Use this provider only when those boundaries are acceptable.

## Custom Providers

Implement `IFileStreamProvider` when ZoneTree files need to live somewhere other than the local file system or the built-in in-memory provider.

Examples include:

* encrypted local storage,
* test harnesses with fault injection,
* remote object stores with local buffering,
* custom storage services,
* embedded environments with their own file abstraction.

A custom provider must return `IFileStream` instances and implement file operations such as existence checks, directory creation, deletion, replacement, and small metadata reads.

```csharp
using ZoneTree.AbstractFileStream;

public sealed class MyFileStreamProvider : IFileStreamProvider
{
    public IFileStream CreateFileStream(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        int bufferSize = 4096,
        FileOptions options = FileOptions.None)
    {
        // Return an IFileStream backed by your storage layer.
        throw new NotImplementedException();
    }

    // Implement the remaining IFileStreamProvider members.
}
```

Then pass the provider to the factory:

```csharp
var provider = new MyFileStreamProvider();

using var zoneTree = new ZoneTreeFactory<int, string>(provider)
    .SetDataDirectory("tenant-a")
    .OpenOrCreate();
```

Focus first on the operations ZoneTree uses heavily:

* sequential WAL append and read,
* random-access segment reads,
* `SetLength`,
* `Flush(bool)`,
* `Replace`,
* directory creation and recursive deletion,
* small metadata reads through `ReadAllBytes` and `ReadAllText`.

The provider's durability model should be clear. If replacement or flush is not durable, document that and use the provider only for rebuildable workloads.
