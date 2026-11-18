using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ZoneTree.AbstractFileStream;

namespace ZoneTree.UnitTests;

/// <summary>
/// Tests the durable file operations on the local file system and in memory. A written file reads back unchanged, a
/// new file never replaces an existing one, a rename replaces a file only when asked to, a replacement does not disturb
/// programs that are reading the replaced file, writes to the same path can run at the same time, and long paths work.
/// </summary>
[TestFixture(true)]
[TestFixture(false)]
public sealed class DurableFileOperationsTests(bool onDisk)
{
  string Root;
  IFileStreamProvider Files;
  IDurableFileOperations Durable;

  [SetUp]
  public void SetUp()
  {
    Root = Path.Combine(Path.GetTempPath(), "zonetree-durable-files", Guid.NewGuid().ToString("N"));
    Files = onDisk ? new LocalFileStreamProvider() : new InMemoryFileStreamProvider();
    Durable = Files.GetDurableFileOperations();
    Files.CreateDirectory(Root);
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(Root))
      Directory.Delete(Root, recursive: true);
  }

  static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

  string PathOf(string name) => Files.CombinePaths(Root, name);

  /// <summary>Writes a file and flushes it to disk, as MoveFile requires before a rename.</summary>
  string Written(string name, string text)
  {
    var path = PathOf(name);
    using var file = Files.CreateFileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    file.Write(Bytes(text));
    file.Flush(flushToDisk: true);
    return path;
  }

  static byte[] ReadAll(IFileStream file)
  {
    using var copy = new MemoryStream();
    file.CopyTo(copy);
    return copy.ToArray();
  }

  [Test]
  public void WriteAllBytesCreatesAFileAndThenReplacesIt()
  {
    var path = PathOf("state");

    Durable.WriteAllBytes(path, Bytes("first"));
    var first = Files.ReadAllBytes(path);
    Durable.WriteAllBytes(path, Bytes("second"));

    Assert.Multiple(() =>
    {
      Assert.That(first, Is.EqualTo(Bytes("first")));
      Assert.That(Files.ReadAllBytes(path), Is.EqualTo(Bytes("second")));
      Assert.That(Files.GetFiles(Root), Has.Count.EqualTo(1));
    });
  }

  [Test]
  public async Task WritesToTheSamePathCanRunAtTheSameTime()
  {
    var path = PathOf("state");
    var contents = Enumerable.Range(0, 8).Select(writer => Bytes($"written by writer {writer}")).ToArray();

    await Task.WhenAll(contents.Select(content => Task.Run(() =>
    {
      for (var write = 0; write < 10; ++write)
        Durable.WriteAllBytes(path, content);
    })));

    Assert.Multiple(() =>
    {
      // The file holds the complete content of one of the writes, and no write left a temporary file behind.
      Assert.That(contents, Has.Some.EqualTo(Files.ReadAllBytes(path)));
      Assert.That(Files.GetFiles(Root), Has.Count.EqualTo(1));
    });
  }

  [Test]
  public async Task OnlyOneOfTheCallsThatCreateTheSameFileAtTheSameTimeSucceeds()
  {
    var path = PathOf("manifest");

    var created = await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
    {
      try
      {
        Durable.WriteNewFile(path, Bytes($"created by writer {writer}"));
        return true;
      }
      catch (IOException)
      {
        return false;
      }
    })));

    Assert.Multiple(() =>
    {
      Assert.That(created.Count(success => success), Is.EqualTo(1));
      Assert.That(Files.GetFiles(Root), Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void WriteNewFileFailsWhenAFileAlreadyExists()
  {
    var path = PathOf("manifest");
    Durable.WriteNewFile(path, Bytes("first"));

    Assert.Throws<IOException>(() => Durable.WriteNewFile(path, Bytes("second")));
    Assert.That(Files.ReadAllBytes(path), Is.EqualTo(Bytes("first")));
  }

  [Test]
  public void MoveFileReplacesAnExistingFileOnlyWhenAskedTo()
  {
    var path = PathOf("manifest");
    Durable.WriteAllBytes(path, Bytes("first"));
    var kept = Written("kept", "kept");
    var replacing = Written("replacing", "second");

    Assert.Catch<IOException>(() => Durable.MoveFile(kept, path, overwrite: false));
    Durable.MoveFile(replacing, path, overwrite: true);

    Assert.Multiple(() =>
    {
      Assert.That(Files.ReadAllBytes(path), Is.EqualTo(Bytes("second")));
      Assert.That(Files.ReadAllBytes(kept), Is.EqualTo(Bytes("kept")));
      Assert.That(Files.FileExists(replacing), Is.False);
    });
  }

  [Test]
  public void ReadersOfAReplacedFileKeepReadingItsOldContent()
  {
    var path = PathOf("manifest");
    Durable.WriteAllBytes(path, Bytes("first"));
    using var read = Files.CreateFileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    Durable.WriteAllBytes(path, Bytes("second"));
    var opened = ReadAll(read);

    Assert.Multiple(() =>
    {
      Assert.That(opened, Is.EqualTo(Bytes("first")));
      Assert.That(Files.ReadAllBytes(path), Is.EqualTo(Bytes("second")));
    });
  }

  [Test]
  public void AFileCanBeOpenedForReadingWhileItIsReplaced()
  {
    var path = PathOf("manifest");
    Durable.WriteAllBytes(path, Bytes("version 0"));
    using var replacing = new CancellationTokenSource();
    var firstRead = new TaskCompletionSource();
    var reads = 0;

    // This reader allows the file to be deleted, as a reader must for the file to be replaced while it is open.
    // Opening the file must never fail while the file is being replaced over and over.
    var reader = Task.Run(() =>
    {
      while (!replacing.IsCancellationRequested)
      {
        using var read = Files.CreateFileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        Assert.That(Encoding.UTF8.GetString(ReadAll(read)), Does.StartWith("version "));
        ++reads;
        firstRead.TrySetResult();
      }
    });
    // Replacements start only after the reader's first read, however late the reader gets a thread; a reader that fails
    // first ends the wait too. The wait blocks instead of awaiting: with a single thread pool thread, the reader would
    // hold it, and code after an await could never resume.
    Task.WaitAny(firstRead.Task, reader);
    try
    {
      for (var version = 1; version <= 200; ++version)
        Durable.WriteAllBytes(path, Bytes($"version {version}"));
    }
    finally
    {
      // The reader stops even when a replacement fails, so the test reports that failure instead of hanging.
      replacing.Cancel();
      reader.GetAwaiter().GetResult();
    }

    Assert.Multiple(() =>
    {
      Assert.That(reads, Is.Positive);
      Assert.That(Files.ReadAllBytes(path), Is.EqualTo(Bytes("version 200")));
    });
  }

  [Test]
  public void AFileWithALongPathIsWrittenAndReplaced()
  {
    var directory = Files.CombinePaths(Root, new string('d', 120));
    Durable.CreateDirectory(directory);
    var path = Files.CombinePaths(directory, new string('f', 150));

    Durable.WriteAllBytes(path, Bytes("first"));
    Durable.WriteAllBytes(path, Bytes("second"));

    Assert.That(Files.ReadAllBytes(path), Is.EqualTo(Bytes("second")));
  }

  [Test]
  public void CreateDirectoryAlsoCreatesMissingParentDirectories()
  {
    var directory = Files.CombinePaths(Files.CombinePaths(Root, "groups"), "group-1");

    Durable.CreateDirectory(directory);
    Durable.CreateDirectory(directory);

    Assert.That(Files.DirectoryExists(directory), Is.True);
  }
}

/// <summary>
/// Tests which directories the durable file operations sync, and how they resolve directory paths. A rename must be
/// followed by a sync of the destination's directory, and of the source's directory when that differs. Creating a
/// directory must sync it and every directory above it, including ones that already existed. Also tests that a failed
/// write cleans up its temporary file without hiding its error.
/// </summary>
public sealed class DurableFileOperationSyncTests
{
  /// <summary>Returns a directory and every directory above it, from the directory up to the root.</summary>
  static List<string> DirectoryAndAncestors(string directory)
  {
    var directories = new List<string>();
    for (var current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
      directories.Add(current);
    return directories;
  }

  /// <summary>
  /// Returns the syncs that the recording provider records for a directory and every directory above it, in the order
  /// in which they are made.
  /// </summary>
  static (string, string)[] SyncsUpToTheRoot(string directory)
      => DirectoryAndAncestors(directory).Select(path => ("sync", path.Replace('\\', '/'))).ToArray();

  [Test]
  public void AWriteWhoseRenameFailsDeletesItsTemporaryFile()
  {
    var files = new RecordingFiles(new InMemoryFileStreamProvider());
    var root = files.CombinePaths(Path.GetTempPath(), "node");
    files.CreateDirectory(root);
    var durable = ((IFileStreamProvider)files).GetDurableFileOperations();
    files.FailRenames = true;

    Assert.Throws<IOException>(() => durable.WriteAllBytes(files.CombinePaths(root, "state"), [1]));

    Assert.That(files.GetFiles(root), Is.Empty);
  }

  [Test]
  public void AFailedCleanupDoesNotHideTheErrorThatMadeTheWriteFail()
  {
    var files = new RecordingFiles(new InMemoryFileStreamProvider());
    var root = files.CombinePaths(Path.GetTempPath(), "node");
    files.CreateDirectory(root);
    var durable = ((IFileStreamProvider)files).GetDurableFileOperations();
    files.FailRenames = true;
    files.FailDeletes = true;

    var failure = Assert.Throws<IOException>(() => durable.WriteAllBytes(files.CombinePaths(root, "state"), [1]));

    Assert.Multiple(() =>
    {
      Assert.That(failure.Message, Is.EqualTo("The rename failed."));
      // The temporary file could not be deleted, so it is left behind.
      Assert.That(files.GetFiles(root), Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void TheDefaultOperationsSyncTheDirectoryAfterEveryRename()
  {
    var files = new RecordingFiles(new InMemoryFileStreamProvider());
    var root = files.CombinePaths(Path.GetTempPath(), "node");
    files.CreateDirectory(root);
    files.Calls.Clear();
    var durable = ((IFileStreamProvider)files).GetDurableFileOperations();
    var state = files.CombinePaths(root, "state");
    var manifest = files.CombinePaths(root, "manifest");

    durable.WriteAllBytes(state, [1]);
    durable.WriteAllBytes(state, [2]);
    durable.WriteNewFile(manifest, [3]);

    Assert.That(files.NameChanges(), Is.EqualTo(new[]
    {
      ("move", state), ("sync", root),
      ("replace", state), ("sync", root),
      ("move", manifest), ("sync", root)
    }));
  }

  [Test]
  public void AMoveToAnotherDirectorySyncsBothDirectories()
  {
    var files = new RecordingFiles(new InMemoryFileStreamProvider());
    var root = files.CombinePaths(Path.GetTempPath(), "node");
    var uploads = files.CombinePaths(root, "uploads");
    var objects = files.CombinePaths(root, "objects");
    files.CreateDirectory(uploads);
    files.CreateDirectory(objects);
    var upload = files.CombinePaths(uploads, "upload");
    var stored = files.CombinePaths(objects, "object");
    var durable = ((IFileStreamProvider)files).GetDurableFileOperations();
    durable.WriteNewFile(upload, [1]);
    files.Calls.Clear();

    durable.MoveFile(upload, stored, overwrite: false);

    Assert.Multiple(() =>
    {
      Assert.That(files.NameChanges(), Is.EqualTo(new[] { ("move", stored), ("sync", objects), ("sync", uploads) }));
      Assert.That(files.ReadAllBytes(stored), Is.EqualTo(new byte[] { 1 }));
    });
  }

  [Test]
  public void TheDefaultOperationsSyncEveryDirectoryOnThePathOfADirectoryTheyCreate()
  {
    var files = new RecordingFiles(new InMemoryFileStreamProvider());
    var root = files.CombinePaths(Path.GetTempPath(), "node");
    files.CreateDirectory(root);
    files.Calls.Clear();
    var durable = ((IFileStreamProvider)files).GetDurableFileOperations();
    var groups = files.CombinePaths(root, "groups");
    var group = files.CombinePaths(groups, "group-1");

    durable.CreateDirectory(group);
    var created = files.NameChanges().ToArray();
    files.Calls.Clear();
    durable.CreateDirectory(group);

    Assert.Multiple(() =>
    {
      // The directory itself first, then each directory above it, up to the root.
      Assert.That(created.Take(3), Is.EqualTo(new[] { ("sync", group), ("sync", groups), ("sync", root) }));
      Assert.That(created[^1], Is.EqualTo(("sync", Path.GetPathRoot(group).Replace('\\', '/'))));
      Assert.That(created, Is.EqualTo(SyncsUpToTheRoot(group)));
      // For an existing directory, the same directories are synced again, in case a crash happened after the
      // directories were created but before their names reached the disk.
      Assert.That(files.NameChanges(), Is.EqualTo(created));
    });
  }

  [Test]
  public void ARetrySyncsTheNamesOfParentDirectoriesThatACrashedCreationLeftBehind()
  {
    var files = new RecordingFiles(new InMemoryFileStreamProvider());
    var root = files.CombinePaths(Path.GetTempPath(), "node");
    files.CreateDirectory(root);
    var parent = files.CombinePaths(root, "a");
    var child = files.CombinePaths(parent, "b");
    // An earlier call created both directories and crashed before it synced any of them.
    files.CreateDirectory(child);
    var durable = ((IFileStreamProvider)files).GetDurableFileOperations();

    durable.CreateDirectory(child);

    Assert.Multiple(() =>
    {
      // The name "a" is stored in root, so root is synced, although every directory on the path already existed.
      Assert.That(files.NameChanges(), Does.Contain(("sync", root)));
      Assert.That(files.NameChanges(), Is.EqualTo(SyncsUpToTheRoot(child)));
    });
  }

  [Test]
  public void AWriteCreatesTheFileWhenItsTargetIsDeletedJustBeforeTheReplacement()
  {
    var files = new RecordingFiles(new InMemoryFileStreamProvider());
    var root = files.CombinePaths(Path.GetTempPath(), "node");
    files.CreateDirectory(root);
    var durable = ((IFileStreamProvider)files).GetDurableFileOperations();
    var state = files.CombinePaths(root, "state");
    durable.WriteAllBytes(state, [1]);
    files.DeleteBeforeTheNextReplacement = true;

    durable.WriteAllBytes(state, [2]);

    Assert.Multiple(() =>
    {
      Assert.That(files.ReadAllBytes(state), Is.EqualTo(new byte[] { 2 }));
      Assert.That(files.GetFiles(root), Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void ADirectoryPathWithoutParentSegmentsReachesTheProviderUnchanged()
  {
    var files = new RecordingFiles(new InMemoryFileStreamProvider());
    var durable = ((IFileStreamProvider)files).GetDurableFileOperations();

    durable.CreateDirectory("s3://bucket/dir");

    Assert.That(files.CreatedDirectories, Is.EqualTo(new[] { "s3://bucket/dir" }));
  }

  [Test]
  public void AnExistingDirectoryWrittenWithATrailingSeparatorHasItsParentSynced()
  {
    var files = new RecordingFiles(new InMemoryFileStreamProvider());
    var root = files.CombinePaths(Path.GetTempPath(), "node");
    var groups = files.CombinePaths(root, "groups");
    files.CreateDirectory(groups);
    files.Calls.Clear();
    var durable = ((IFileStreamProvider)files).GetDurableFileOperations();

    durable.CreateDirectory(groups + "/");

    Assert.Multiple(() =>
    {
      // The name "groups" is stored in its parent, so the parent is synced. The directory itself is synced once,
      // under its name without the separator.
      Assert.That(files.NameChanges(), Does.Contain(("sync", root)));
      Assert.That(files.NameChanges(), Is.EqualTo(SyncsUpToTheRoot(groups)));
    });
  }

  [Test]
  public void ADirectoryWithParentSegmentsIsCreatedAndSyncedOnItsResolvedPath()
  {
    var files = new RecordingFiles(new InMemoryFileStreamProvider());
    var root = files.CombinePaths(Path.GetTempPath(), "node");
    files.CreateDirectory(root);
    var durable = ((IFileStreamProvider)files).GetDurableFileOperations();
    var data = files.CombinePaths(root, "data");

    // "missing" never exists, so a sync of "missing" or of "missing/.." would fail on a real file system.
    durable.CreateDirectory(root + "/missing/../data");

    Assert.Multiple(() =>
    {
      Assert.That(files.DirectoryExists(data), Is.True);
      Assert.That(files.DirectoryExists(files.CombinePaths(root, "missing")), Is.False);
      Assert.That(files.NameChanges(), Is.EqualTo(SyncsUpToTheRoot(data)));
    });
  }

  [TestCase("a/./b", "a/b")]
  [TestCase("a/../b", "b")]
  [TestCase("a/b/../../c", "c")]
  [TestCase("../a", "../a")]
  [TestCase("a/../..", "..")]
  [TestCase("a/..", ".")]
  [TestCase("a//b/../c", "a/c")]
  [TestCase("/a/../../b", "/b")]
  [TestCase("/a/..", "/")]
  // A path without "." or ".." segments is returned exactly as it is.
  [TestCase("a//b/", "a//b/")]
  [TestCase("s3://bucket/dir", "s3://bucket/dir")]
  public void ParentSegmentsAreResolvedFromThePathAlone(string path, string expected)
  {
    var resolved = DirectorySync.WithoutDotSegments(path.Replace('/', Path.DirectorySeparatorChar));

    Assert.That(resolved.Replace('\\', '/'), Is.EqualTo(expected));
  }

  [Test]
  [Platform("Win")]
  public void ParentSegmentsAreResolvedWithoutAddingADriveOnWindows()
  {
    Assert.Multiple(() =>
    {
      // Path.GetFullPath would turn "/node/a/../b" into "C:\node\b". The rewritten path keeps its separator.
      Assert.That(DirectorySync.WithoutDotSegments("/node/a/../b"), Is.EqualTo("/node/b"));
      Assert.That(DirectorySync.WithoutDotSegments("//server/share/a/../b"), Is.EqualTo("//server/share/b"));
      Assert.That(DirectorySync.WithoutDotSegments("s3://bucket/dir"), Is.EqualTo("s3://bucket/dir"));
      Assert.That(DirectorySync.WithoutDotSegments(@"C:\a\..\b"), Is.EqualTo(@"C:\b"));
      Assert.That(DirectorySync.WithoutDotSegments(@"C:a\..\b"), Is.EqualTo(@"C:b"));
      // "D:" is the current directory on drive D, so a ".." after it leads to that directory's parent, and stays.
      Assert.That(DirectorySync.WithoutDotSegments(@"D:..\data"), Is.EqualTo(@"D:..\data"));
      Assert.That(DirectorySync.WithoutDotSegments(@"D:a\..\..\b"), Is.EqualTo(@"D:..\b"));
      // A ".." at a real root stays at that root.
      Assert.That(DirectorySync.WithoutDotSegments(@"C:\..\b"), Is.EqualTo(@"C:\b"));
      Assert.That(DirectorySync.WithoutDotSegments(@"\..\b"), Is.EqualTo(@"\b"));
      Assert.That(DirectorySync.WithoutDotSegments(@"\\server\share\..\b"), Is.EqualTo(@"\\server\share\b"));
      Assert.That(DirectorySync.WithoutDotSegments(@"\\server\share\a\..\b"), Is.EqualTo(@"\\server\share\b"));
      Assert.That(DirectorySync.WithoutDotSegments(@"\\?\C:\a\..\b"), Is.EqualTo(@"\\?\C:\a\..\b"));
    });
  }

  [Test]
  public void CreateDirectorySkipsADirectoryAboveItThatTheProcessMayNotOpen()
  {
    var files = new RecordingFiles(new InMemoryFileStreamProvider());
    var root = files.CombinePaths(Path.GetTempPath(), "node");
    files.CreateDirectory(root);
    var durable = ((IFileStreamProvider)files).GetDurableFileOperations();
    var group = files.CombinePaths(files.CombinePaths(root, "groups"), "group-1");
    files.DeniedDirectories.Add(root);

    durable.CreateDirectory(group);

    Assert.That(files.NameChanges(), Is.EqualTo(SyncsUpToTheRoot(group).Where(sync => sync.Item2 != root)));
  }

  [Test]
  public void CreateDirectoryFailsWhenADirectoryAboveItCannotBeSynced()
  {
    var files = new RecordingFiles(new InMemoryFileStreamProvider());
    var root = files.CombinePaths(Path.GetTempPath(), "node");
    files.CreateDirectory(root);
    var durable = ((IFileStreamProvider)files).GetDurableFileOperations();
    files.FailingDirectories.Add(root);

    Assert.Throws<IOException>(() => durable.CreateDirectory(files.CombinePaths(root, "groups")));
  }

  [Test]
  public void ADirectoryWithARelativePathHasItsNameSyncedInTheCurrentDirectory()
  {
    var files = new RecordingFiles(new InMemoryFileStreamProvider());
    var durable = ((IFileStreamProvider)files).GetDurableFileOperations();

    durable.CreateDirectory("relative-directory");

    Assert.That(files.NameChanges(), Is.EqualTo(new[] { ("sync", "relative-directory"), ("sync", ".") }));
  }

  [Test]
  public void TheLocalOperationsSyncTheDirectoriesTheyChange()
  {
    var root = Path.Combine(Path.GetTempPath(), "zonetree-durable-files", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var synced = new List<string>();
      var durable = new LocalDurableFileOperations(new LocalFileStreamProvider(), synced.Add);
      var group = Path.Combine(root, "groups", "group-1");

      durable.CreateDirectory(group);
      var created = synced.ToArray();
      synced.Clear();
      durable.WriteAllBytes(Path.Combine(group, "state"), [1]);

      Assert.Multiple(() =>
      {
        Assert.That(created.Take(3), Is.EqualTo(new[] { group, Path.Combine(root, "groups"), root }));
        Assert.That(created, Is.EqualTo(DirectoryAndAncestors(group)));
        Assert.That(synced, Is.EqualTo(new[] { group }));
      });
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Test]
  public void TheLocalOperationsSyncTheDirectoriesOnTheResolvedPathOfAPathWithParentSegments()
  {
    var root = Path.Combine(Path.GetTempPath(), "zonetree-durable-files", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var synced = new List<string>();
      var durable = new LocalDurableFileOperations(new LocalFileStreamProvider(), synced.Add);

      // Directory creation resolves "..", so this creates only "data", and "missing" never exists.
      durable.CreateDirectory(Path.Combine(root, "missing", "..", "data"));

      Assert.Multiple(() =>
      {
        Assert.That(Directory.Exists(Path.Combine(root, "data")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(root, "missing")), Is.False);
        Assert.That(synced, Is.EqualTo(DirectoryAndAncestors(Path.Combine(root, "data"))));
        Assert.That(synced, Has.None.Contains("missing"));
      });
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>
  /// A file stream provider that records the renames, directory syncs and directory creations made through it, and
  /// can be told to fail in the ways the tests need.
  /// </summary>
  sealed class RecordingFiles(IFileStreamProvider files) : IFileStreamProvider
  {
    internal List<(string Operation, string Path)> Calls { get; } = [];

    /// <summary>When true, every rename throws an <see cref="IOException"/>.</summary>
    internal bool FailRenames { get; set; }

    /// <summary>
    /// When true, every delete throws an <see cref="InvalidOperationException"/>, an error that is not one of the usual
    /// file errors.
    /// </summary>
    internal bool FailDeletes { get; set; }

    /// <summary>
    /// When true, the next replacement first deletes the file it would replace, as another call might at that moment,
    /// and then fails with a <see cref="FileNotFoundException"/>, as the contract of Replace allows.
    /// </summary>
    internal bool DeleteBeforeTheNextReplacement { get; set; }

    /// <summary>The paths that directories were created at, exactly as the provider received them.</summary>
    internal List<string> CreatedDirectories { get; } = [];

    /// <summary>
    /// The directories that the process may not open, written with '/'. Their syncs throw an
    /// <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    internal HashSet<string> DeniedDirectories { get; } = [];

    /// <summary>The directories whose syncs fail, written with '/'. Their syncs throw an <see cref="IOException"/>.</summary>
    internal HashSet<string> FailingDirectories { get; } = [];

    void FailIfAsked()
    {
      if (FailRenames)
        throw new IOException("The rename failed.");
    }

    void Record(string operation, string path) => Calls.Add((operation, path.Replace('\\', '/')));

    /// <summary>Returns the recorded renames and directory syncs, each with its path.</summary>
    internal IEnumerable<(string, string)> NameChanges() => Calls.Where(call => call.Operation is "move" or "replace" or "sync");

    public IFileStream CreateFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize = 4096,
        FileOptions options = FileOptions.None) => files.CreateFileStream(path, mode, access, share, bufferSize, options);

    public bool FileExists(string path) => files.FileExists(path);

    public bool DirectoryExists(string path) => files.DirectoryExists(path);

    public void CreateDirectory(string path)
    {
      CreatedDirectories.Add(path);
      files.CreateDirectory(path);
    }

    public void SyncDirectory(string path)
    {
      var written = path.Replace('\\', '/');
      if (DeniedDirectories.Contains(written))
        throw new UnauthorizedAccessException($"The process may not open '{path}'.");
      if (FailingDirectories.Contains(written))
        throw new IOException($"Could not sync '{path}'.");
      Record("sync", path);
      files.SyncDirectory(path);
    }

    public void DeleteFile(string path)
    {
      if (FailDeletes)
        throw new InvalidOperationException("The delete failed.");
      files.DeleteFile(path);
    }

    public void DeleteDirectory(string path, bool recursive) => files.DeleteDirectory(path, recursive);

    public string ReadAllText(string path) => files.ReadAllText(path);

    public byte[] ReadAllBytes(string path) => files.ReadAllBytes(path);

    public void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName)
    {
      FailIfAsked();
      if (DeleteBeforeTheNextReplacement)
      {
        DeleteBeforeTheNextReplacement = false;
        files.DeleteFile(destinationFileName);
        throw new FileNotFoundException("No file exists at the destination.", destinationFileName);
      }
      Record("replace", destinationFileName);
      files.Replace(sourceFileName, destinationFileName, destinationBackupFileName);
    }

    public DurableFileWriter GetDurableFileWriter() => files.GetDurableFileWriter();

    public IReadOnlyList<string> GetDirectories(string path) => files.GetDirectories(path);

    public IReadOnlyList<string> GetFiles(string path) => files.GetFiles(path);

    public void MoveFile(string sourcePath, string destinationPath)
    {
      FailIfAsked();
      Record("move", destinationPath);
      files.MoveFile(sourcePath, destinationPath);
    }

    public string CombinePaths(string path1, string path2) => files.CombinePaths(path1, path2);
  }
}

/// <summary>
/// Tests the Windows rename. A file held open by another process blocks the rename, which is retried for a few
/// seconds and then fails, leaving the original file unchanged. A file that replaces another one keeps its own access
/// rules, and a symbolic link is renamed itself.
/// </summary>
[Platform("Win")]
[SupportedOSPlatform("windows")]
public sealed class WindowsFileRenameTests
{
  readonly IDurableFileOperations Durable = new LocalFileStreamProvider().GetDurableFileOperations();
  string Root;

  [SetUp]
  public void SetUp()
  {
    Root = Path.Combine(Path.GetTempPath(), "zonetree-durable-files", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Root);
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(Root))
      Directory.Delete(Root, recursive: true);
  }

  static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

  /// <summary>
  /// Opens a file the way a virus scanner or another program might: for reading, without allowing it to be deleted or
  /// renamed while it is open.
  /// </summary>
  static FileStream Hold(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

  [Test]
  public async Task ARenameWaitsForADestinationThatIsHeldForAMoment()
  {
    var path = Path.Combine(Root, "state");
    Durable.WriteAllBytes(path, Bytes("first"));
    var held = Hold(path);

    var write = Task.Run(() => Durable.WriteAllBytes(path, Bytes("second")));
    await Task.Delay(30);
    await held.DisposeAsync();
    await write;

    Assert.That(File.ReadAllBytes(path), Is.EqualTo(Bytes("second")));
  }

  [Test]
  public async Task ARenameWaitsForASourceThatIsHeldForAMoment()
  {
    var source = Path.Combine(Root, "upload");
    var destination = Path.Combine(Root, "object");
    File.WriteAllBytes(source, Bytes("upload"));
    var held = Hold(source);

    var move = Task.Run(() => Durable.MoveFile(source, destination, overwrite: false));
    await Task.Delay(30);
    await held.DisposeAsync();
    await move;

    Assert.Multiple(() =>
    {
      Assert.That(File.ReadAllBytes(destination), Is.EqualTo(Bytes("upload")));
      Assert.That(File.Exists(source), Is.False);
    });
  }

  /// <summary>
  /// Gives a file a single access rule, for the current user, and stops it from inheriting the access rules of its
  /// directory.
  /// </summary>
  static void RestrictToTheCurrentUser(string path)
  {
    var security = new FileSecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.AddAccessRule(new FileSystemAccessRule(
        WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, AccessControlType.Allow));
    new FileInfo(path).SetAccessControl(security);
  }

  /// <summary>Returns the access rules of a file, both its own and the ones that it inherits.</summary>
  static (string, FileSystemRights, AccessControlType, bool)[] AccessRules(string path)
      => new FileInfo(path).GetAccessControl()
          .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
          .Cast<FileSystemAccessRule>()
          .Select(rule => (rule.IdentityReference.Value, rule.FileSystemRights, rule.AccessControlType, rule.IsInherited))
          .ToArray();

  [Test]
  public void AFileThatReplacesAnotherKeepsItsOwnAccessRules()
  {
    var path = Path.Combine(Root, "state");
    File.WriteAllBytes(path, Bytes("first"));
    RestrictToTheCurrentUser(path);
    var source = Path.Combine(Root, "new");
    File.WriteAllBytes(source, Bytes("second"));
    var own = AccessRules(source);

    new LocalFileStreamProvider().Replace(source, path, null);

    // A rename never copies the replaced file's rules, so permissions belong on the directory.
    Assert.That(AccessRules(path), Is.EqualTo(own));
  }

  [Test]
  public void ARenameMovesASymbolicLinkItselfAndNotTheFileItPointsTo()
  {
    var target = Path.Combine(Root, "target");
    File.WriteAllBytes(target, Bytes("target"));
    var link = Path.Combine(Root, "link");
    try
    {
      File.CreateSymbolicLink(link, target);
    }
    catch (IOException)
    {
      Assert.Ignore("Creating a symbolic link needs Developer Mode or an administrator on Windows.");
    }
    catch (UnauthorizedAccessException)
    {
      Assert.Ignore("Creating a symbolic link needs Developer Mode or an administrator on Windows.");
    }
    var moved = Path.Combine(Root, "moved");

    Durable.MoveFile(link, moved, overwrite: false);

    Assert.Multiple(() =>
    {
      Assert.That(new FileInfo(moved).LinkTarget, Is.EqualTo(target));
      Assert.That(File.Exists(link), Is.False);
      Assert.That(File.ReadAllBytes(target), Is.EqualTo(Bytes("target")));
    });
  }

  [Test]
  public void ARenameThatStaysBlockedFailsAndKeepsTheOldFile()
  {
    var path = Path.Combine(Root, "state");
    Durable.WriteAllBytes(path, Bytes("first"));

    using (Hold(path))
    {
      Assert.That(() => Durable.WriteAllBytes(path, Bytes("second")),
          Throws.InstanceOf<IOException>());
    }

    Assert.Multiple(() =>
    {
      Assert.That(File.ReadAllBytes(path), Is.EqualTo(Bytes("first")));
      // The failed write deleted its temporary file.
      Assert.That(Directory.GetFiles(Root), Has.Length.EqualTo(1));
    });
  }
}

/// <summary>
/// Tests the directory sync on Linux and macOS with a directory that its owner may pass through but not read. Syncing
/// it reports that access is denied, and creating a directory below it still succeeds, because the walk up to the root
/// skips it.
/// </summary>
[Platform(Exclude = "Win")]
[UnsupportedOSPlatform("windows")]
public sealed class UnixDirectorySyncTests
{
  const UnixFileMode PassThroughOnly = UnixFileMode.UserWrite | UnixFileMode.UserExecute;

  const UnixFileMode Full = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

  string Root;

  string Unreadable;

  [SetUp]
  public void SetUp()
  {
    Root = Path.Combine(Path.GetTempPath(), "zonetree-directory-sync", Guid.NewGuid().ToString("N"));
    Unreadable = Path.Combine(Root, "unreadable");
    Directory.CreateDirectory(Unreadable);
    File.SetUnixFileMode(Unreadable, PassThroughOnly);
    if (!CanList(Unreadable))
      return;
    // NUnit skips TearDown when SetUp ends the test, so this cleans up first.
    TearDown();
    Assert.Ignore("This test runs with privileges that ignore directory permissions.");
  }

  [TearDown]
  public void TearDown()
  {
    File.SetUnixFileMode(Unreadable, Full);
    Directory.Delete(Root, recursive: true);
  }

  static bool CanList(string directory)
  {
    try
    {
      Directory.GetFileSystemEntries(directory);
      return true;
    }
    catch (UnauthorizedAccessException)
    {
      return false;
    }
  }

  [Test]
  public void SyncingADirectoryThatMayNotBeReadReportsThatAccessIsDenied()
  {
    Assert.Throws<UnauthorizedAccessException>(() => new LocalFileStreamProvider().SyncDirectory(Unreadable));
  }

  [Test]
  public void ADirectoryBelowADirectoryThatMayNotBeReadIsCreated()
  {
    var durable = new LocalFileStreamProvider().GetDurableFileOperations();
    var data = Path.Combine(Unreadable, "data");

    durable.CreateDirectory(data);
    DirectorySync.FlushTree(data, new LocalFileStreamProvider().SyncDirectory);

    Assert.That(Directory.Exists(data), Is.True);
  }
}

/// <summary>
/// Tests the moves on Linux and macOS. A move that must not replace a file succeeds on a free path and fails on a
/// taken one, leaving both files unchanged, with every fallback. The system's own rename that refuses to replace a
/// file is present and really refuses. A replacing move fails like File.Move on a missing file. The move by link moves
/// a symbolic link itself, removes its new name again when it cannot remove the old one, and leaves only a name that is
/// safe to delete when a crash interrupts it.
/// </summary>
[Platform(Exclude = "Win")]
[UnsupportedOSPlatform("windows")]
public sealed class UnixFileMoveTests
{
  string Root;

  [SetUp]
  public void SetUp()
  {
    Root = Path.Combine(Path.GetTempPath(), "zonetree-durable-files", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Root);
  }

  [TearDown]
  public void TearDown()
  {
    if (Directory.Exists(Root))
      Directory.Delete(Root, recursive: true);
  }

  [Test]
  public void AFileMovesToAPathWhereNoFileExists()
  {
    var source = Path.Combine(Root, "manifest.tmp");
    var destination = Path.Combine(Root, "manifest");
    File.WriteAllText(source, "new");

    UnixFileMove.MoveWithoutReplacing(source, destination, NoReplaceFallback.Fail);

    Assert.Multiple(() =>
    {
      Assert.That(File.ReadAllText(destination), Is.EqualTo("new"));
      Assert.That(File.Exists(source), Is.False);
    });
  }

  [Test]
  public void AMoveOntoAnExistingFileFailsAndLeavesBothFiles()
  {
    var source = Path.Combine(Root, "manifest.tmp");
    var destination = Path.Combine(Root, "manifest");
    File.WriteAllText(source, "new");
    File.WriteAllText(destination, "old");

    Assert.Throws<IOException>(() => UnixFileMove.MoveWithoutReplacing(source, destination, NoReplaceFallback.Fail));

    Assert.Multiple(() =>
    {
      Assert.That(File.ReadAllText(destination), Is.EqualTo("old"));
      Assert.That(File.ReadAllText(source), Is.EqualTo("new"));
    });
  }

  [Test]
  public void AMoveOfAMissingFileFailsLikeFileMove()
  {
    var source = Path.Combine(Root, "missing");

    Assert.Throws<FileNotFoundException>(() => UnixFileMove.MoveWithoutReplacing(source, Path.Combine(Root, "manifest"), NoReplaceFallback.Fail));
  }

  [Test]
  public void AMoveIntoAMissingDirectoryFailsLikeFileMove()
  {
    var source = Path.Combine(Root, "upload");
    File.WriteAllText(source, "upload");

    Assert.Throws<DirectoryNotFoundException>(
        () => UnixFileMove.MoveWithoutReplacing(source, Path.Combine(Root, "missing", "object"), NoReplaceFallback.Fail));
    Assert.That(File.ReadAllText(source), Is.EqualTo("upload"));
  }

  [Test]
  public void ATemporaryFileMovesToAPathWhereNoFileExistsAndNeverReplacesOne()
  {
    var temporary = Path.Combine(Root, "manifest.0123456789abcdef.tmp");
    var destination = Path.Combine(Root, "manifest");
    var taken = Path.Combine(Root, "taken");
    File.WriteAllText(temporary, "new");
    File.WriteAllText(taken, "taken");

    UnixFileMove.MoveWithoutReplacing(temporary, destination, NoReplaceFallback.HardLink);
    Assert.Throws<IOException>(() => UnixFileMove.MoveWithoutReplacing(destination, taken, NoReplaceFallback.HardLink));

    Assert.Multiple(() =>
    {
      Assert.That(File.ReadAllText(destination), Is.EqualTo("new"));
      Assert.That(File.Exists(temporary), Is.False);
      Assert.That(File.ReadAllText(taken), Is.EqualTo("taken"));
    });
  }

  [Test]
  public void AMoveByLinkOfAMissingFileFailsLikeFileMove()
  {
    var source = Path.Combine(Root, "missing");

    Assert.Throws<FileNotFoundException>(() => UnixFileMove.MoveByLink(source, Path.Combine(Root, "manifest")));
  }

  [Test]
  public void AMoveThatMayFallBackToFileMoveStillRefusesToReplaceAFile()
  {
    var source = Path.Combine(Root, "upload");
    var destination = Path.Combine(Root, "object");
    var taken = Path.Combine(Root, "taken");
    File.WriteAllText(source, "upload");
    File.WriteAllText(taken, "taken");

    UnixFileMove.MoveWithoutReplacing(source, destination, NoReplaceFallback.CheckFirst);
    Assert.Throws<IOException>(() => UnixFileMove.MoveWithoutReplacing(destination, taken, NoReplaceFallback.CheckFirst));

    Assert.Multiple(() =>
    {
      Assert.That(File.ReadAllText(destination), Is.EqualTo("upload"));
      Assert.That(File.Exists(source), Is.False);
      Assert.That(File.ReadAllText(taken), Is.EqualTo("taken"));
    });
  }

  [Test]
  public void AReplacingMoveReplacesAFileOrCreatesOne()
  {
    var target = Path.Combine(Root, "manifest");
    var first = Path.Combine(Root, "first");
    var second = Path.Combine(Root, "second");
    File.WriteAllText(first, "first");
    File.WriteAllText(second, "second");

    UnixFileMove.MoveReplacing(first, target);
    var created = File.ReadAllText(target);
    UnixFileMove.MoveReplacing(second, target);

    Assert.Multiple(() =>
    {
      Assert.That(created, Is.EqualTo("first"));
      Assert.That(File.ReadAllText(target), Is.EqualTo("second"));
      Assert.That(File.Exists(first), Is.False);
      Assert.That(File.Exists(second), Is.False);
    });
  }

  [Test]
  public void AReplacingMoveOfAMissingFileFailsLikeFileMove()
  {
    Assert.Throws<FileNotFoundException>(
        () => UnixFileMove.MoveReplacing(Path.Combine(Root, "missing"), Path.Combine(Root, "manifest")));
  }

  [Test]
  public void AMoveByLinkMovesASymbolicLinkItselfAndNotTheFileItPointsTo()
  {
    var target = Path.Combine(Root, "target");
    File.WriteAllText(target, "target");
    var link = Path.Combine(Root, "link");
    File.CreateSymbolicLink(link, target);
    var destination = Path.Combine(Root, "moved");

    UnixFileMove.MoveByLink(link, destination);

    Assert.Multiple(() =>
    {
      Assert.That(new FileInfo(destination).LinkTarget, Is.EqualTo(target));
      Assert.That(File.Exists(link), Is.False);
      Assert.That(File.ReadAllText(target), Is.EqualTo("target"));
    });
  }

  [Test]
  public void TheSystemRenameMovesAFileInOneStep()
  {
    var source = Path.Combine(Root, "upload");
    var destination = Path.Combine(Root, "object");
    File.WriteAllText(source, "upload");
    IgnoreUnlessTheSystemHasARenameThatRefusesToReplace();

    // Zero means that the system's own rename moved the file, so the move did not fall back to a move by link.
    Assert.That(UnixFileMove.RenameWithoutReplacing(source, destination), Is.Zero);

    Assert.Multiple(() =>
    {
      Assert.That(File.ReadAllText(destination), Is.EqualTo("upload"));
      Assert.That(File.Exists(source), Is.False);
    });
  }

  [Test]
  public void TheSystemRenameRefusesToReplaceAFile()
  {
    var source = Path.Combine(Root, "upload");
    var destination = Path.Combine(Root, "object");
    File.WriteAllText(source, "new");
    File.WriteAllText(destination, "old");
    IgnoreUnlessTheSystemHasARenameThatRefusesToReplace();

    // 17 is EEXIST on Linux and macOS. A rename without the option that refuses to replace a file would succeed here.
    Assert.That(UnixFileMove.RenameWithoutReplacing(source, destination), Is.EqualTo(17));

    Assert.Multiple(() =>
    {
      Assert.That(File.ReadAllText(destination), Is.EqualTo("old"));
      Assert.That(File.ReadAllText(source), Is.EqualTo("new"));
    });
  }

  [Test]
  public void AMoveByLinkMovesAFileAndRefusesToReplaceOne()
  {
    var source = Path.Combine(Root, "upload");
    var destination = Path.Combine(Root, "object");
    var taken = Path.Combine(Root, "taken");
    File.WriteAllText(source, "upload");
    File.WriteAllText(taken, "taken");

    UnixFileMove.MoveByLink(source, destination);
    Assert.Throws<IOException>(() => UnixFileMove.MoveByLink(destination, taken));

    Assert.Multiple(() =>
    {
      Assert.That(File.ReadAllText(destination), Is.EqualTo("upload"));
      Assert.That(File.Exists(source), Is.False);
      Assert.That(File.ReadAllText(taken), Is.EqualTo("taken"));
    });
  }

  [Test]
  public void AMoveByLinkThatCannotRemoveTheOldNameRemovesTheNewNameAgain()
  {
    var sources = Path.Combine(Root, "sources");
    Directory.CreateDirectory(sources);
    var source = Path.Combine(sources, "upload");
    var destination = Path.Combine(Root, "object");
    File.WriteAllText(source, "upload");
    // The names in a directory without write permission cannot be removed. The file can still be linked elsewhere,
    // because the link is created in another directory.
    File.SetUnixFileMode(sources, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    try
    {
      if (CanCreateAFileIn(sources))
        Assert.Ignore("This test runs with privileges that ignore directory permissions.");

      Assert.That(() => UnixFileMove.MoveByLink(source, destination), Throws.InstanceOf<UnauthorizedAccessException>());

      Assert.Multiple(() =>
      {
        Assert.That(File.ReadAllText(source), Is.EqualTo("upload"));
        Assert.That(File.Exists(destination), Is.False);
      });
    }
    finally
    {
      File.SetUnixFileMode(sources, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
  }

  /// <summary>Skips the test on systems without a rename that refuses to replace a file, such as FreeBSD.</summary>
  static void IgnoreUnlessTheSystemHasARenameThatRefusesToReplace()
  {
    if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
      Assert.Ignore("This system has no rename that refuses to replace a file.");
  }

  /// <summary>Returns true if a file can be created in the directory, which a privileged user can always do.</summary>
  static bool CanCreateAFileIn(string directory)
  {
    var probe = Path.Combine(directory, "probe");
    try
    {
      File.WriteAllText(probe, "");
      File.Delete(probe);
      return true;
    }
    catch (UnauthorizedAccessException)
    {
      return false;
    }
  }

  [Test]
  public void ATemporaryFileLeftByAnInterruptedMoveIsSafeToDelete()
  {
    var durable = new LocalFileStreamProvider().GetDurableFileOperations();
    var path = Path.Combine(Root, "manifest");
    durable.WriteNewFile(path, "first"u8);
    // A crash in the middle of a move by link would leave the file under both names: its final path and the temporary
    // path of the write.
    var leftover = path + ".0123456789abcdef.tmp";
    Assert.That(UnixFileMove.Link(path, leftover), Is.Zero);

    durable.WriteAllBytes(path, "second"u8);
    var leftoverContent = File.ReadAllText(leftover);
    File.Delete(leftover);

    Assert.Multiple(() =>
    {
      // The write created a new file, so the leftover name still has the earlier content. Deleting it removed only
      // that name.
      Assert.That(leftoverContent, Is.EqualTo("first"));
      Assert.That(File.ReadAllText(path), Is.EqualTo("second"));
    });
  }
}
