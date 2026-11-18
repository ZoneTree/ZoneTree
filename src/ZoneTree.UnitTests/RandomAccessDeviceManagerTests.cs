using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Segments.RandomAccess;

namespace ZoneTree.UnitTests;

/// <summary>
/// Tests how the random-access device manager handles an existing file when it creates a writable device in its
/// place. When asked to keep a backup, it renames the existing file to a new backup name instead of deleting it.
/// </summary>
[TestFixture(true)]
[TestFixture(false)]
public sealed class RandomAccessDeviceManagerTests(bool onDisk)
{
  string Root;
  IFileStreamProvider Files;

  [SetUp]
  public void SetUp()
  {
    Root = Path.Combine(Path.GetTempPath(), "zonetree-device-manager", Guid.NewGuid().ToString("N"));
    Files = onDisk ? new LocalFileStreamProvider() : new InMemoryFileStreamProvider();
    Files.CreateDirectory(Root);
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(Root))
      Directory.Delete(Root, recursive: true);
  }

  [Test]
  public void ADeviceCreatedInPlaceOfAFileKeepsThatFileAsABackup()
  {
    var manager = new RandomAccessDeviceManager(new ConsoleLogger(LogLevel.Error), Files, Root);
    var path = manager.GetFilePath(1, ".data");
    Files.GetDurableFileOperations().WriteAllBytes(path, [1, 2, 3]);

    var device = manager.CreateWritableDevice(1, ".data", isCompressed: false, compressionBlockSize: 0,
        deleteIfExists: true, backupIfDelete: true, CompressionMethod.None, compressionLevel: 0);
    manager.CloseAllDevices();

    var backups = Files.GetFiles(Root).Where(file => file.Contains(".data.backup.", StringComparison.Ordinal)).ToArray();
    Assert.Multiple(() =>
    {
      Assert.That(device, Is.Not.Null);
      Assert.That(backups, Has.Length.EqualTo(1));
      Assert.That(Files.ReadAllBytes(backups[0]), Is.EqualTo(new byte[] { 1, 2, 3 }));
    });
  }
}
