using ZoneTree.AbstractFileStream;
using ZoneTree.Comparers;
using ZoneTree.Exceptions;
using ZoneTree.Logging;
using ZoneTree.Options;
using ZoneTree.Segments.RandomAccess;
using ZoneTree.Serializers;
using ZoneTree.WAL;
using ZoneTree.Hashers;

namespace ZoneTree.UnitTests;

public sealed class ZoneTreeOptionsValidationTests
{
  [Test]
  public void ValidOptionsPassValidation()
  {
    var options = CreateValidOptions();

    Assert.That(options.TryValidate(out var exception), Is.True);
    Assert.That(exception, Is.Null);
  }

  [TestCaseSource(nameof(MissingOptionCases))]
  public void MissingRequiredOptionsFailValidation(
      Action<ZoneTreeOptions<int, int>> configure,
      string option)
  {
    var options = CreateValidOptions();
    configure(options);

    var isValid = options.TryValidate(out var exception);

    Assert.That(isValid, Is.False);
    var missingOptionException = Assert.Throws<MissingOptionException>(() => options.Validate());
    Assert.That(exception, Is.TypeOf<MissingOptionException>());
    Assert.That(((MissingOptionException)exception).MissingOption, Is.EqualTo(option));
    Assert.That(missingOptionException.MissingOption, Is.EqualTo(option));
  }

  [TestCaseSource(nameof(InvalidOptionCases))]
  public void InvalidOptionValuesFailValidation(
      Action<ZoneTreeOptions<int, int>> configure,
      string option)
  {
    var options = CreateValidOptions();
    configure(options);

    var isValid = options.TryValidate(out var exception);

    Assert.That(isValid, Is.False);
    var invalidOptionException = Assert.Throws<InvalidOptionValueException>(() => options.Validate());
    Assert.That(exception, Is.TypeOf<InvalidOptionValueException>());
    Assert.That(((InvalidOptionValueException)exception).Option, Is.EqualTo(option));
    Assert.That(invalidOptionException.Option, Is.EqualTo(option));
  }

  [Test]
  public void InvalidCompressionLevelStillUsesCompressionLevelException()
  {
    var options = CreateValidOptions();
    options.DiskSegmentOptions.CompressionMethod = CompressionMethod.Zstd;
    options.DiskSegmentOptions.CompressionLevel = CompressionLevels.ZstdMax + 1;

    var isValid = options.TryValidate(out var exception);

    Assert.That(isValid, Is.False);
    Assert.That(exception, Is.TypeOf<CompressionLevelIsOutOfRangeException>());
    Assert.Throws<CompressionLevelIsOutOfRangeException>(() => options.Validate());
  }

  [Test]
  public void MaximumMutableSegmentBloomFilterBitsPerItemPassesValidation()
  {
    var options = CreateValidOptions();
    options.MutableSegmentBloomFilterBitsPerItem = 64;

    Assert.That(options.TryValidate(out var exception), Is.True);
    Assert.That(exception, Is.Null);
  }

  [Test]
  public void MissingKeyHasherPassesValidationWithoutBloomFilterAndWithOneLock()
  {
    var options = CreateValidOptions();
    options.KeyHasher = null;
    options.MutableSegmentBloomFilterBitsPerItem = 0;
    options.LockStripeCount = 1;

    Assert.That(options.TryValidate(out var exception), Is.True);
    Assert.That(exception, Is.Null);
  }

  [Test]
  public void MissingKeyHasherExplainsHowToUseOneLock()
  {
    var options = CreateValidOptions();
    options.KeyHasher = null;
    options.MutableSegmentBloomFilterBitsPerItem = 0;

    var exception = Assert.Throws<MissingOptionException>(options.Validate);

    Assert.That(exception.Message, Does.Contain($"set {nameof(options.LockStripeCount)} to 1"));
  }

  [Test]
  public void MissingKeyHasherExplainsHowToDisableBloomFilter()
  {
    var options = CreateValidOptions();
    options.KeyHasher = null;

    var exception = Assert.Throws<MissingOptionException>(options.Validate);

    Assert.That(
        exception.Message,
        Does.Contain($"set {nameof(options.MutableSegmentBloomFilterBitsPerItem)} to 0"));
  }

  [Test]
  public void AllowUnsafeOptionValuesSkipsNumericRanges()
  {
    var options = CreateValidOptions();
    options.AllowUnsafeOptionValues = true;
    options.MutableSegmentMaxItemCount = 1;
    options.DiskSegmentMaxItemCount = 1;
    options.BPlusTreeNodeSize = 2;
    options.BPlusTreeLeafSize = 2;
    options.WriteAheadLogOptions.CompressionBlockSize = 1;
    options.DiskSegmentOptions.CompressionBlockSize = 1;
    options.DiskSegmentOptions.MinimumRecordCount = 10;
    options.DiskSegmentOptions.MaximumRecordCount = 1;

    Assert.That(options.TryValidate(out var exception), Is.True);
    Assert.That(exception, Is.Null);
  }

  [Test]
  public void AllowUnsafeOptionValuesKeepsEnumAndCompressionValidation()
  {
    var options = CreateValidOptions();
    options.AllowUnsafeOptionValues = true;
    options.WriteAheadLogOptions.Mode = (WriteAheadLogMode)99;

    Assert.That(options.TryValidate(out var exception), Is.False);
    Assert.That(exception, Is.TypeOf<InvalidOptionValueException>());
  }

  [TestCase(true)]
  [TestCase(false)]
  public void AllowUnsafeOptionValuesKeepsTheIntervalValidation(bool syncJobInterval)
  {
    var options = CreateValidOptions();
    options.AllowUnsafeOptionValues = true;
    if (syncJobInterval)
      options.WriteAheadLogOptions.SyncJobInterval = TimeSpan.FromMilliseconds(-2);
    else
      options.WriteAheadLogOptions.EmptyQueuePollInterval = TimeSpan.FromMilliseconds(-2);

    Assert.That(options.TryValidate(out var exception), Is.False);
    Assert.That(exception, Is.TypeOf<InvalidOptionValueException>());
  }

  [Test]
  public void CaseInsensitiveComparerRejectsIncompatibleKeyHasher()
  {
    var options = CreateValidStringOptions();
    options.Comparer = new StringOrdinalIgnoreCaseComparerAscending();
    options.KeyHasher = new DefaultKeyHasher<string>();

    var isValid = options.TryValidate(out var exception);

    Assert.That(isValid, Is.False);
    Assert.That(exception, Is.TypeOf<InvalidOptionValueException>());
    Assert.That(
        ((InvalidOptionValueException)exception).Option,
        Is.EqualTo(nameof(options.KeyHasher)));
  }

  [Test]
  public void CaseInsensitiveComparerAcceptsCompatibleKeyHasher()
  {
    var options = CreateValidStringOptions();
    options.Comparer = new StringOrdinalIgnoreCaseComparerAscending();
    options.KeyHasher = new OrdinalIgnoreCaseKeyHasher();

    Assert.That(options.TryValidate(out var exception), Is.True);
    Assert.That(exception, Is.Null);
  }

  static IEnumerable<TestCaseData> MissingOptionCases()
  {
    yield return Missing(
        options => options.KeySerializer = null,
        nameof(ZoneTreeOptions<int, int>.KeySerializer));

    yield return Missing(
        options => options.ValueSerializer = null,
        nameof(ZoneTreeOptions<int, int>.ValueSerializer));

    yield return Missing(
        options => options.Comparer = null,
        nameof(ZoneTreeOptions<int, int>.Comparer));

    yield return Missing(
        options => options.KeyHasher = null,
        nameof(ZoneTreeOptions<int, int>.KeyHasher));

    yield return Missing(
        options => options.IsDeleted = null,
        nameof(ZoneTreeOptions<int, int>.IsDeleted));

    yield return Missing(
        options => options.MarkValueDeleted = null,
        nameof(ZoneTreeOptions<int, int>.MarkValueDeleted));

    yield return Missing(
        options => options.Logger = null,
        nameof(ZoneTreeOptions<int, int>.Logger));

    yield return Missing(
        options => options.RandomAccessDeviceManager = null,
        nameof(ZoneTreeOptions<int, int>.RandomAccessDeviceManager));

    yield return Missing(
        options => options.WriteAheadLogProvider = null,
        nameof(ZoneTreeOptions<int, int>.WriteAheadLogProvider));

    yield return Missing(
        options => options.WriteAheadLogOptions = null,
        nameof(ZoneTreeOptions<int, int>.WriteAheadLogOptions));

    yield return Missing(
        options => options.DiskSegmentOptions = null,
        nameof(ZoneTreeOptions<int, int>.DiskSegmentOptions));

    yield return Missing(
        options => options.WriteAheadLogOptions.SharedModeOptions = null,
        "WriteAheadLogOptions.SharedModeOptions");
  }

  static IEnumerable<TestCaseData> InvalidOptionCases()
  {
    yield return Invalid(
        options => options.MutableSegmentMaxItemCount = 0,
        nameof(ZoneTreeOptions<int, int>.MutableSegmentMaxItemCount));

    yield return Invalid(
        options => options.MutableSegmentBloomFilterBitsPerItem = -1,
        nameof(ZoneTreeOptions<int, int>.MutableSegmentBloomFilterBitsPerItem));

    yield return Invalid(
        options => options.MutableSegmentBloomFilterBitsPerItem = 65,
        nameof(ZoneTreeOptions<int, int>.MutableSegmentBloomFilterBitsPerItem));

    yield return Invalid(
        options => options.DiskSegmentMaxItemCount = 0,
        nameof(ZoneTreeOptions<int, int>.DiskSegmentMaxItemCount));

    yield return Invalid(
        options => options.BPlusTreeLockMode = (ZoneTree.Collections.BPlusTreeLockMode)99,
        nameof(ZoneTreeOptions<int, int>.BPlusTreeLockMode));

    yield return Invalid(
        options => options.BPlusTreeNodeSize = 1,
        nameof(ZoneTreeOptions<int, int>.BPlusTreeNodeSize));

    yield return Invalid(
        options => options.BPlusTreeLeafSize = 1,
        nameof(ZoneTreeOptions<int, int>.BPlusTreeLeafSize));

    yield return Invalid(
        options => options.WriteAheadLogOptions.Mode = (WriteAheadLogMode)99,
        "WriteAheadLogOptions.Mode");

    yield return Invalid(
        options => options.WriteAheadLogOptions.CompressionMethod = (CompressionMethod)99,
        "WriteAheadLogOptions.CompressionMethod");

    yield return Invalid(
        options => options.WriteAheadLogOptions.CompressionBlockSize = 0,
        "WriteAheadLogOptions.CompressionBlockSize");

    yield return Invalid(
        options => options.WriteAheadLogOptions.CompressionBlockSize = 16 * 1024 * 1024 + 1,
        "WriteAheadLogOptions.CompressionBlockSize");

    yield return Invalid(
        options => options.WriteAheadLogOptions.EmptyQueuePollInterval = TimeSpan.FromMilliseconds(-1),
        "WriteAheadLogOptions.EmptyQueuePollInterval");

    yield return Invalid(
        options => options.WriteAheadLogOptions.SyncJobInterval = TimeSpan.FromMilliseconds(-1),
        "WriteAheadLogOptions.SyncJobInterval");

    yield return Invalid(
        options => options.DiskSegmentOptions.Mode = (DiskSegmentMode)99,
        "DiskSegmentOptions.Mode");

    yield return Invalid(
        options => options.DiskSegmentOptions.CompressionMethod = (CompressionMethod)99,
        "DiskSegmentOptions.CompressionMethod");

    yield return Invalid(
        options => options.DiskSegmentOptions.CompressionBlockSize = 0,
        "DiskSegmentOptions.CompressionBlockSize");

    yield return Invalid(
        options => options.DiskSegmentOptions.CompressionBlockSize = 64 * 1024 * 1024 + 1,
        "DiskSegmentOptions.CompressionBlockSize");

    yield return Invalid(
        options => options.DiskSegmentOptions.MaximumRecordCount = 0,
        "DiskSegmentOptions.MaximumRecordCount");

    yield return Invalid(
        options => options.DiskSegmentOptions.MinimumRecordCount = 0,
        "DiskSegmentOptions.MinimumRecordCount");

    yield return Invalid(
        options => options.DiskSegmentOptions.MinimumRecordCount =
            options.DiskSegmentOptions.MaximumRecordCount,
        "DiskSegmentOptions.MinimumRecordCount");

    yield return Invalid(
        options => options.DiskSegmentOptions.KeyCacheSize = -1,
        "DiskSegmentOptions.KeyCacheSize");

    yield return Invalid(
        options => options.DiskSegmentOptions.ValueCacheSize = -1,
        "DiskSegmentOptions.ValueCacheSize");

    yield return Invalid(
        options => options.DiskSegmentOptions.MaterializedEntryCacheSize = -1,
        "DiskSegmentOptions.MaterializedEntryCacheSize");

    yield return Invalid(
        options => options.DiskSegmentOptions.SearchHintPrefetchSize = -1,
        "DiskSegmentOptions.SearchHintPrefetchSize");

    yield return Invalid(
        options => options.DiskSegmentOptions.KeyCacheRecordLifetime = TimeSpan.FromMilliseconds(-1),
        "DiskSegmentOptions.KeyCacheRecordLifetime");

    yield return Invalid(
        options => options.DiskSegmentOptions.ValueCacheRecordLifetime = TimeSpan.FromMilliseconds(-1),
        "DiskSegmentOptions.ValueCacheRecordLifetime");

    yield return Invalid(
        options => options.DiskSegmentOptions.DefaultSparseArrayStepSize = -1,
        "DiskSegmentOptions.DefaultSparseArrayStepSize");
  }

  static TestCaseData Missing(
      Action<ZoneTreeOptions<int, int>> configure,
      string option)
  {
    return new TestCaseData(configure, option).SetName($"Missing {option}");
  }

  static TestCaseData Invalid(
      Action<ZoneTreeOptions<int, int>> configure,
      string option)
  {
    return new TestCaseData(configure, option).SetName($"Invalid {option}");
  }

  static ZoneTreeOptions<int, int> CreateValidOptions()
  {
    var logger = new ConsoleLogger(ZoneTreeLogLevel.Error);
    var options = new ZoneTreeOptions<int, int>
    {
      Comparer = new Int32ComparerAscending(),
      KeyHasher = new DefaultKeyHasher<int>(),
      KeySerializer = new Int32Serializer(),
      ValueSerializer = new Int32Serializer(),
      Logger = logger,
      WriteAheadLogProvider = new NullWriteAheadLogProvider(),
      RandomAccessDeviceManager = new RandomAccessDeviceManager(
          logger,
          new InMemoryFileStreamProvider(),
          "data/ZoneTreeOptionsValidationTests")
    };
    options.DisableDeletion();
    return options;
  }

  static ZoneTreeOptions<string, string> CreateValidStringOptions()
  {
    var logger = new ConsoleLogger(ZoneTreeLogLevel.Error);
    var options = new ZoneTreeOptions<string, string>
    {
      Comparer = new StringOrdinalComparerAscending(),
      KeyHasher = new DefaultKeyHasher<string>(),
      KeySerializer = new Utf8StringSerializer(),
      ValueSerializer = new Utf8StringSerializer(),
      Logger = logger,
      WriteAheadLogProvider = new NullWriteAheadLogProvider(),
      RandomAccessDeviceManager = new RandomAccessDeviceManager(
          logger,
          new InMemoryFileStreamProvider(),
          "data/ZoneTreeStringOptionsValidationTests")
    };
    options.DisableDeletion();
    return options;
  }

  sealed class OrdinalIgnoreCaseKeyHasher : IKeyHasher<string>
  {
    public int GetHashCode(in string key)
    {
      return StringComparer.OrdinalIgnoreCase.GetHashCode(key);
    }
  }
}
