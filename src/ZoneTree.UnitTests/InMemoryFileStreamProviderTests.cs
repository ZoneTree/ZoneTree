using ZoneTree.AbstractFileStream;
using ZoneTree.Logging;
using ZoneTree.Options;
using ZoneTree.Serializers;
using ZoneTree.WAL;

namespace ZoneTree.UnitTests;

public sealed class InMemoryFileStreamProviderTests
{
  [Test]
  public void WritesAreVisibleBeforeStreamIsDisposed()
  {
    var provider = new InMemoryFileStreamProvider();
    var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None);
    var bytes = new byte[] { 1, 2, 3 };

    stream.Write(bytes, 0, bytes.Length);
    stream.Flush(true);

    Assert.That(provider.ReadAllBytes("test.bin"), Is.EqualTo(bytes));
    stream.Dispose();
  }

  [Test]
  public void DisposingDeletedStreamDoesNotRestoreFile()
  {
    var provider = new InMemoryFileStreamProvider();
    var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None);
    stream.WriteByte(1);

    provider.DeleteFile("test.bin");
    stream.Dispose();

    Assert.That(provider.FileExists("test.bin"), Is.False);
  }

  [Test]
  public void SetLengthImmediatelyChangesProviderContent()
  {
    var provider = new InMemoryFileStreamProvider();
    using var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None);
    var bytes = new byte[] { 1, 2, 3, 4 };
    stream.Write(bytes, 0, bytes.Length);

    stream.SetLength(2);

    Assert.That(provider.ReadAllBytes("test.bin"), Is.EqualTo(new byte[] { 1, 2 }));
  }

  [Test]
  public void SetLengthExtendsWithZeroBytes()
  {
    var provider = new InMemoryFileStreamProvider();
    using var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None);
    stream.WriteByte(7);

    stream.SetLength(4);

    Assert.That(provider.ReadAllBytes("test.bin"), Is.EqualTo(new byte[] { 7, 0, 0, 0 }));
    Assert.That(stream.Length, Is.EqualTo(4));
  }

  [Test]
  public void SetLengthClearsBytesWhenReExtending()
  {
    var provider = new InMemoryFileStreamProvider();
    using var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None);
    stream.Write(new byte[] { 1, 2, 3, 4 });

    stream.SetLength(2);
    stream.SetLength(4);

    Assert.That(provider.ReadAllBytes("test.bin"), Is.EqualTo(new byte[] { 1, 2, 0, 0 }));
  }

  [Test]
  public void SparseWriteFillsGapWithZeroBytes()
  {
    var provider = new InMemoryFileStreamProvider();
    using var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None);

    stream.Position = 3;
    stream.WriteByte(9);

    Assert.That(provider.ReadAllBytes("test.bin"), Is.EqualTo(new byte[] { 0, 0, 0, 9 }));
  }

  [Test]
  public void SparseWriteAfterTruncateClearsGapWithZeroBytes()
  {
    var provider = new InMemoryFileStreamProvider();
    using var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None);
    stream.Write(new byte[] { 1, 2, 3, 4 });
    stream.SetLength(1);

    stream.Position = 3;
    stream.WriteByte(9);

    Assert.That(provider.ReadAllBytes("test.bin"), Is.EqualTo(new byte[] { 1, 0, 0, 9 }));
  }

  [Test]
  public void ReadAndWriteCanCrossSmallChunkBoundaryBeforePromotion()
  {
    const int smallChunkSize = 4 * 1024;
    var provider = new InMemoryFileStreamProvider();
    using var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None);
    var expected = new byte[] { 1, 2, 3, 4 };

    stream.Position = smallChunkSize - 2;
    stream.Write(expected);
    stream.Position = smallChunkSize - 2;
    var actual = new byte[expected.Length];
    stream.ReadFaster(actual, 0, actual.Length);

    Assert.That(stream.Length, Is.EqualTo(smallChunkSize + 2));
    Assert.That(actual, Is.EqualTo(expected));
  }

  [Test]
  public void ReadAndWriteCanCrossLargeChunkBoundaryAfterPromotion()
  {
    const int chunkSize = 8 * 1024 * 1024;
    var provider = new InMemoryFileStreamProvider();
    using var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None);
    var expected = new byte[] { 1, 2, 3, 4 };

    stream.Position = chunkSize - 2;
    stream.Write(expected);
    stream.Position = chunkSize - 2;
    var actual = new byte[expected.Length];
    stream.ReadFaster(actual, 0, actual.Length);

    Assert.That(stream.Length, Is.EqualTo(chunkSize + 2));
    Assert.That(actual, Is.EqualTo(expected));
  }

  [Test]
  public void PromotionKeepsExistingSmallChunkContent()
  {
    const int largeChunkThreshold = 1024 * 1024;
    var provider = new InMemoryFileStreamProvider();
    using var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None);
    stream.Write(new byte[] { 1, 2, 3 });

    stream.Position = largeChunkThreshold + 1;
    stream.WriteByte(4);

    Assert.That(stream.Length, Is.EqualTo(largeChunkThreshold + 2));
    Assert.That(provider.ReadAllBytes("test.bin")[..3], Is.EqualTo(new byte[] { 1, 2, 3 }));
    stream.Position = largeChunkThreshold + 1;
    Assert.That(stream.ReadByte(), Is.EqualTo(4));
  }

  [Test]
  public void SetLengthZeroAfterPromotionResetsToSmallFileBehavior()
  {
    const int largeChunkThreshold = 1024 * 1024;
    var provider = new InMemoryFileStreamProvider();
    using var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None);
    stream.Position = largeChunkThreshold + 1;
    stream.WriteByte(7);

    stream.SetLength(0);
    stream.Write(new byte[] { 1, 2, 3 });

    Assert.That(provider.ReadAllBytes("test.bin"), Is.EqualTo(new byte[] { 1, 2, 3 }));
  }

  [Test]
  public void AppendAlwaysWritesAtEnd()
  {
    var provider = new InMemoryFileStreamProvider();
    WriteFile(provider, "test.bin", new byte[] { 1, 2 });
    using var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Append,
        FileAccess.Write,
        FileShare.None);

    stream.Position = 0;
    stream.WriteByte(3);

    Assert.That(provider.ReadAllBytes("test.bin"), Is.EqualTo(new byte[] { 1, 2, 3 }));
  }

  [Test]
  public void ReadFasterReadsExactLengthOrThrows()
  {
    var provider = new InMemoryFileStreamProvider();
    WriteFile(provider, "test.bin", new byte[] { 1, 2, 3 });
    using var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Open,
        FileAccess.Read,
        FileShare.None);
    var buffer = new byte[4];

    Assert.That(stream.ReadFaster(buffer, 0, 3), Is.EqualTo(3));
    Assert.That(buffer.Take(3).ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
    Assert.Throws<EndOfStreamException>(() => stream.ReadFaster(buffer, 0, 1));
  }

  [Test]
  public async Task AsyncReadAndWriteUseSharedProviderMemory()
  {
    var provider = new InMemoryFileStreamProvider();
    await using (var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None))
    {
      await stream.WriteAsync(new byte[] { 1, 2 }, 0, 2);
      await stream.WriteAsync(new byte[] { 3 });
    }

    await using var readStream = provider.CreateFileStream(
        "test.bin",
        FileMode.Open,
        FileAccess.Read,
        FileShare.None);
    var buffer = new byte[3];

    Assert.That(await readStream.ReadAsync(buffer, 0, buffer.Length), Is.EqualTo(3));
    Assert.That(buffer, Is.EqualTo(new byte[] { 1, 2, 3 }));
  }

  [Test]
  public void ReplaceMovesSourceToDestinationAndBacksUpOldDestination()
  {
    var provider = new InMemoryFileStreamProvider();
    WriteFile(provider, "target.bin", new byte[] { 1, 2 });
    WriteFile(provider, "tmp.bin", new byte[] { 3, 4 });

    provider.Replace("tmp.bin", "target.bin", "backup.bin");

    Assert.That(provider.FileExists("tmp.bin"), Is.False);
    Assert.That(provider.ReadAllBytes("target.bin"), Is.EqualTo(new byte[] { 3, 4 }));
    Assert.That(provider.ReadAllBytes("backup.bin"), Is.EqualTo(new byte[] { 1, 2 }));
  }

  [Test]
  public void ReplaceBackupKeepsPreviousDestination()
  {
    var provider = new InMemoryFileStreamProvider();
    WriteFile(provider, "target.bin", new byte[] { 1 });
    WriteFile(provider, "tmp.bin", new byte[] { 2 });

    provider.Replace("tmp.bin", "target.bin", "backup.bin");

    Assert.That(provider.ReadAllBytes("backup.bin"), Is.EqualTo(new byte[] { 1 }));
    Assert.That(provider.ReadAllBytes("target.bin"), Is.EqualTo(new byte[] { 2 }));
  }

  [Test]
  public void ReplaceRequiresSourceFile()
  {
    var provider = new InMemoryFileStreamProvider();

    Assert.Throws<FileNotFoundException>(() => provider.Replace(
        "missing.bin",
        "target.bin",
        null));
  }

  [Test]
  public void CreateNewFailsWhenFileAlreadyExists()
  {
    var provider = new InMemoryFileStreamProvider();
    WriteFile(provider, "test.bin", new byte[] { 1 });

    Assert.Throws<IOException>(() => provider.CreateFileStream(
        "test.bin",
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None));
  }

  [Test]
  public void TruncateRequiresExistingFileAndClearsImmediately()
  {
    var provider = new InMemoryFileStreamProvider();
    WriteFile(provider, "test.bin", new byte[] { 1, 2 });

    using var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Truncate,
        FileAccess.Write,
        FileShare.None);

    Assert.That(stream.Length, Is.EqualTo(0));
    Assert.That(provider.ReadAllBytes("test.bin"), Is.Empty);
    Assert.Throws<FileNotFoundException>(() => provider.CreateFileStream(
        "missing.bin",
        FileMode.Truncate,
        FileAccess.Write,
        FileShare.None));
  }

  [Test]
  public void ReadOnlyAndWriteOnlyAccessAreEnforced()
  {
    var provider = new InMemoryFileStreamProvider();
    WriteFile(provider, "test.bin", new byte[] { 1 });

    using var readOnly = provider.CreateFileStream(
        "test.bin",
        FileMode.Open,
        FileAccess.Read,
        FileShare.None);
    using var writeOnly = provider.CreateFileStream(
        "test.bin",
        FileMode.Open,
        FileAccess.Write,
        FileShare.None);

    Assert.Throws<NotSupportedException>(() => readOnly.WriteByte(2));
    Assert.Throws<NotSupportedException>(() => writeOnly.ReadByte());
  }

  [Test]
  public void DisposedStreamRejectsOperations()
  {
    var provider = new InMemoryFileStreamProvider();
    var stream = provider.CreateFileStream(
        "test.bin",
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None);

    stream.Dispose();

    Assert.Throws<ObjectDisposedException>(() => _ = stream.Length);
    Assert.Throws<ObjectDisposedException>(() => stream.WriteByte(1));
    Assert.That(stream.CanRead, Is.False);
    Assert.That(stream.CanWrite, Is.False);
    Assert.That(stream.CanSeek, Is.False);
  }

  [Test]
  public void RecursiveDirectoryDeleteUsesPathSegments()
  {
    var provider = new InMemoryFileStreamProvider();
    WriteFile(provider, "data/file.bin", new byte[] { 1 });
    WriteFile(provider, "database/file.bin", new byte[] { 2 });

    provider.DeleteDirectory("data", true);

    Assert.That(provider.FileExists("data/file.bin"), Is.False);
    Assert.That(provider.FileExists("database/file.bin"), Is.True);
  }

  [Test]
  public void PathsAreNormalizedAcrossSeparators()
  {
    var provider = new InMemoryFileStreamProvider();
    WriteFile(provider, "data\\nested//file.bin", new byte[] { 1 });

    Assert.That(provider.FileExists("data/nested/file.bin"), Is.True);
    Assert.That(provider.ReadAllBytes("data/nested\\file.bin"), Is.EqualTo(new byte[] { 1 }));
  }

  [Test]
  public void NonRecursiveDirectoryDeleteRejectsNonEmptyDirectory()
  {
    var provider = new InMemoryFileStreamProvider();
    WriteFile(provider, "data/file.bin", new byte[] { 1 });

    Assert.Throws<IOException>(() => provider.DeleteDirectory("data", false));
    Assert.That(provider.FileExists("data/file.bin"), Is.True);
  }

  [Test]
  public void GetDirectoriesReturnsImmediateChildrenOnly()
  {
    var provider = new InMemoryFileStreamProvider();
    provider.CreateDirectory("data/a/b");
    provider.CreateDirectory("data/c");

    Assert.That(
        provider.GetDirectories("data").Order().ToArray(),
        Is.EqualTo(new[] { "data/a", "data/c" }));
  }

  [Test]
  public void WalWithInMemoryProvider()
  {
    var serializer = new UnicodeStringSerializer();
    var provider = new InMemoryFileStreamProvider();
    var options = new WriteAheadLogOptions();
    options.EnableSyncJob = false;
    var wal = new PrivateWriteAheadLog<string, string>(
        new ConsoleLogger(),
        provider,
        serializer,
        serializer,
        "test.wal",
        options);
    wal.Append("hello", "world", 0);
    var result = wal.ReadLogEntries(false, false, true);
    Assert.That(result.Success, Is.True);
    Assert.That(result.Keys[0], Is.EqualTo("hello"));
    Assert.That(result.Values[0], Is.EqualTo("world"));
    wal.Drop();
  }

  static void WriteFile(
      InMemoryFileStreamProvider provider,
      string path,
      byte[] bytes)
  {
    using var stream = provider.CreateFileStream(
        path,
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None);
    stream.Write(bytes, 0, bytes.Length);
  }
}
