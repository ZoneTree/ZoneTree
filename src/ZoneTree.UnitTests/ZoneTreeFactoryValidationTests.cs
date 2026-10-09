using ZoneTree.Exceptions;

namespace ZoneTree.UnitTests;

public sealed class ZoneTreeFactoryValidationTests
{
  sealed class CustomValue
  {
  }

  [Test]
  public void ValidOptionsPassWithoutTouchingTheFileSystem()
  {
    var walPath = CreatePath();
    var factory = new ZoneTreeFactory<int, string>()
        .SetWriteAheadLogDirectory(walPath)
        .SetMutableSegmentMaxItemCount(10_000);

    Assert.That(factory.Validate(), Is.SameAs(factory));
    Assert.Multiple(() =>
    {
      Assert.That(Directory.Exists(walPath), Is.False);
      // Storage components are created when the tree is opened, not by validation.
      Assert.That(factory.Options.RandomAccessDeviceManager, Is.Null);
      Assert.That(factory.Options.WriteAheadLogProvider, Is.Null);
      // Default components of known types are filled in as OpenOrCreate fills them.
      Assert.That(factory.Options.Comparer, Is.Not.Null);
      Assert.That(factory.Options.KeySerializer, Is.Not.Null);
      Assert.That(factory.Options.ValueSerializer, Is.Not.Null);
    });
  }

  [Test]
  public void InvalidOptionValuesFail()
  {
    var factory = new ZoneTreeFactory<int, int>()
        .SetDiskSegmentMaxItemCount(1_000);

    var exception = Assert.Throws<InvalidOptionValueException>(() => factory.Validate());
    Assert.That(exception.Option, Does.Contain(nameof(factory.Options.DiskSegmentMaxItemCount)));
  }

  [Test]
  public void UnsafeOptionValuesPassWhenAllowed()
  {
    var factory = new ZoneTreeFactory<int, int>()
        .SetDiskSegmentMaxItemCount(1_000)
        .Configure(options => options.AllowUnsafeOptionValues = true);

    Assert.DoesNotThrow(() => factory.Validate());
  }

  [Test]
  public void InvalidNestedOptionsFail()
  {
    var factory = new ZoneTreeFactory<int, int>()
        .ConfigureWriteAheadLogOptions(options => options.AsyncCompressedModeOptions = null);

    Assert.Throws<MissingOptionException>(() => factory.Validate());
  }

  [Test]
  public void MissingComponentsOfUnknownTypesFail()
  {
    var factory = new ZoneTreeFactory<int, CustomValue>();

    var exception = Assert.Throws<MissingOptionException>(() => factory.Validate());
    Assert.That(exception.MissingOption, Is.EqualTo(nameof(factory.Options.ValueSerializer)));
  }

  [Test]
  public void ValidatedFactoryOpensTheTree()
  {
    var dataPath = CreatePath();
    try
    {
      using var zoneTree = new ZoneTreeFactory<int, int>()
          .SetDataDirectory(dataPath)
          .Validate()
          .OpenOrCreate();
      zoneTree.Upsert(1, 2);
      Assert.That(zoneTree.TryGet(1, out var value) ? value : 0, Is.EqualTo(2));
    }
    finally
    {
      if (Directory.Exists(dataPath))
        Directory.Delete(dataPath, true);
    }
  }

  static string CreatePath()
  {
    return Path.Combine(
        "data",
        $"{nameof(ZoneTreeFactoryValidationTests)}_{Guid.NewGuid():N}");
  }
}
