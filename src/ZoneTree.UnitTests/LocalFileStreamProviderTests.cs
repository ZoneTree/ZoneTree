using ZoneTree.AbstractFileStream;

namespace ZoneTree.UnitTests;

/// <summary>
/// Tests how the local provider replaces and moves files. A replacement that keeps a backup is retried for a few
/// seconds when it fails because another process has a file open, and fails immediately for any other reason. A
/// replacement without a backup is an atomic rename on every system: it also works when the target does not exist yet,
/// and when programs that allow the target to be deleted are reading it. A move never replaces a file.
/// </summary>
public sealed class LocalFileStreamProviderTests
{
  [Test]
  public void ReplaceRetriesTransientIoExceptionAndEventuallySucceeds()
  {
    var attemptCount = 0;
    var delays = new List<int>();
    var provider = new LocalFileStreamProvider(
        (_, _, _) =>
        {
          ++attemptCount;
          if (attemptCount < 3)
            throw Held();
        },
        delays.Add);

    provider.Replace("source", "destination", "backup");

    Assert.Multiple(() =>
    {
      Assert.That(attemptCount, Is.EqualTo(3));
      Assert.That(delays, Is.EqualTo(HeldFileRetry.DelaysMilliseconds.Take(2)));
    });
  }

  [Test]
  public void ReplaceRethrowsAfterBoundedRetryAttempts()
  {
    var attemptCount = 0;
    var delays = new List<int>();
    var provider = new LocalFileStreamProvider(
        (_, _, _) =>
        {
          ++attemptCount;
          throw Held();
        },
        delays.Add);

    Assert.Throws<IOException>(() => provider.Replace("source", "destination", "backup"));

    Assert.Multiple(() =>
    {
      // One first attempt, then one retry after each wait in the schedule.
      Assert.That(attemptCount, Is.EqualTo(HeldFileRetry.DelaysMilliseconds.Length + 1));
      Assert.That(delays, Is.EqualTo(HeldFileRetry.DelaysMilliseconds));
    });
  }

  [Test]
  public void ReplaceDoesNotRetryMissingSourceFile()
  {
    var attemptCount = 0;
    var delays = new List<int>();
    var provider = new LocalFileStreamProvider(
        (_, _, _) =>
        {
          ++attemptCount;
          throw new FileNotFoundException("source");
        },
        delays.Add);

    Assert.Throws<FileNotFoundException>(() => provider.Replace("source", "destination", "backup"));

    Assert.Multiple(() =>
    {
      Assert.That(attemptCount, Is.EqualTo(1));
      Assert.That(delays, Is.Empty);
    });
  }

  [TestCase(0x8007_0070, TestName = "A full disk")]
  [TestCase(0x8007_0005, TestName = "Access denied")]
  [TestCase(16, TestName = "A busy file on Linux or macOS")]
  public void ReplaceDoesNotRetryAFailureThatIsNotAHeldFile(long result)
  {
    var attemptCount = 0;
    var delays = new List<int>();
    var provider = new LocalFileStreamProvider(
        (_, _, _) =>
        {
          ++attemptCount;
          throw new IOException("The replacement failed.", unchecked((int)result));
        },
        delays.Add);

    Assert.Throws<IOException>(() => provider.Replace("source", "destination", "backup"));

    Assert.Multiple(() =>
    {
      Assert.That(attemptCount, Is.EqualTo(1));
      Assert.That(delays, Is.Empty);
    });
  }

  [Test]
  public void ReplaceRetriesAReplacementThatAnotherProcessKeptFromBeingMovedIntoPlace()
  {
    var attemptCount = 0;
    var delays = new List<int>();
    var provider = new LocalFileStreamProvider(
        (_, _, _) =>
        {
          ++attemptCount;
          // ERROR_UNABLE_TO_MOVE_REPLACEMENT: with a backup, every file still has its original name.
          if (attemptCount == 1)
            throw new IOException("Unable to move the replacement file.", unchecked((int)0x8007_0498));
        },
        delays.Add);

    provider.Replace("source", "destination", "backup");

    Assert.Multiple(() =>
    {
      Assert.That(attemptCount, Is.EqualTo(2));
      Assert.That(delays, Is.EqualTo(HeldFileRetry.DelaysMilliseconds.Take(1)));
    });
  }

  [Test]
  public void ReplaceDoesNotRetryAReplacementThatAlreadyMovedTheReplacedFileAway()
  {
    var attemptCount = 0;
    var provider = new LocalFileStreamProvider(
        (_, _, _) =>
        {
          ++attemptCount;
          // ERROR_UNABLE_TO_MOVE_REPLACEMENT_2: the replaced file already has the backup name.
          throw new IOException("Unable to move the replacement file.", unchecked((int)0x8007_0499));
        },
        _ => { });

    Assert.Throws<IOException>(() => provider.Replace("source", "destination", "backup"));

    Assert.That(attemptCount, Is.EqualTo(1));
  }

  [Test]
  public void MoveFileMovesAFileAndNeverReplacesOne()
  {
    var root = NewDirectory();
    try
    {
      var provider = new LocalFileStreamProvider();
      var source = Path.Combine(root, "upload");
      var destination = Path.Combine(root, "object");
      var taken = Path.Combine(root, "taken");
      File.WriteAllText(source, "upload");
      File.WriteAllText(taken, "taken");

      provider.MoveFile(source, destination);
      Assert.Throws<IOException>(() => provider.MoveFile(destination, taken));

      Assert.Multiple(() =>
      {
        Assert.That(File.ReadAllText(destination), Is.EqualTo("upload"));
        Assert.That(File.Exists(source), Is.False);
        Assert.That(File.ReadAllText(taken), Is.EqualTo("taken"));
      });
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Test]
  public void MoveFileOfAMissingFileFailsLikeFileMove()
  {
    var root = NewDirectory();
    try
    {
      var provider = new LocalFileStreamProvider();

      Assert.Throws<FileNotFoundException>(
          () => provider.MoveFile(Path.Combine(root, "missing"), Path.Combine(root, "object")));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>Returns the error Windows reports when another process has the file open: a sharing violation.</summary>
  static IOException Held()
      => new("The process cannot access the file because it is being used by another process.", unchecked((int)0x8007_0020));

  [Test]
  public void AReplacementWithoutABackupRenamesOverTheTargetOrToANewName()
  {
    var root = NewDirectory();
    try
    {
      var provider = new LocalFileStreamProvider();
      var target = Path.Combine(root, "meta.json");
      File.WriteAllText(target, "old");
      File.WriteAllText(target + ".tmp", "new");
      var backup = target + ".backup";

      provider.Replace(target + ".tmp", target, null);
      // No file exists at the backup path yet, so this replacement is a plain rename to a new name.
      provider.Replace(target, backup, null);

      Assert.Multiple(() =>
      {
        Assert.That(File.ReadAllText(backup), Is.EqualTo("new"));
        Assert.That(File.Exists(target), Is.False);
        Assert.That(File.Exists(target + ".tmp"), Is.False);
      });
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Test]
  public void AReplacementWithoutABackupLeavesReadersThatAllowDeletionReading()
  {
    var root = NewDirectory();
    try
    {
      var provider = new LocalFileStreamProvider();
      var target = Path.Combine(root, "meta.json");
      File.WriteAllText(target, "old");
      File.WriteAllText(target + ".tmp", "new");
      using var reader = new StreamReader(new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));

      provider.Replace(target + ".tmp", target, null);

      Assert.Multiple(() =>
      {
        Assert.That(reader.ReadToEnd(), Is.EqualTo("old"));
        Assert.That(File.ReadAllText(target), Is.EqualTo("new"));
      });
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  static string NewDirectory()
  {
    var root = Path.Combine(Path.GetTempPath(), "zonetree-local-provider", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    return root;
  }
}
