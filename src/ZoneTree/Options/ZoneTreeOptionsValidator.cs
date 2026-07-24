using ZoneTree.Exceptions;

namespace ZoneTree.Options;

internal static class ZoneTreeOptionsValidator
{
  public static bool TryValidate<TKey, TValue>(
      ZoneTreeOptions<TKey, TValue> options,
      out Exception exception)
  {
    exception = Validate(options);
    return exception == null;
  }

  static Exception Validate<TKey, TValue>(ZoneTreeOptions<TKey, TValue> options)
  {
    if (options.KeySerializer == null)
      return new MissingOptionException(nameof(options.KeySerializer));

    if (options.ValueSerializer == null)
      return new MissingOptionException(nameof(options.ValueSerializer));

    if (options.Comparer == null)
      return new MissingOptionException(nameof(options.Comparer));

    var mutableSegmentBloomFilterEnabled =
        options.MutableSegmentBloomFilterBitsPerItem > 0;
    if (mutableSegmentBloomFilterEnabled && options.KeyHasher == null)
    {
      return new MissingOptionException(
          nameof(options.KeyHasher),
          $"Configure a compatible key hasher or set " +
          $"{nameof(options.MutableSegmentBloomFilterBitsPerItem)} to 0 " +
          "to disable the mutable-segment Bloom filter.");
    }

    if (options.IsDeleted == null)
      return new MissingOptionException(nameof(options.IsDeleted));

    if (options.MarkValueDeleted == null)
      return new MissingOptionException(nameof(options.MarkValueDeleted));

    if (options.Logger == null)
      return new MissingOptionException(nameof(options.Logger));

    if (options.RandomAccessDeviceManager == null)
      return new MissingOptionException(nameof(options.RandomAccessDeviceManager));

    if (options.WriteAheadLogProvider == null)
      return new MissingOptionException(nameof(options.WriteAheadLogProvider));

    if (options.WriteAheadLogOptions == null)
      return new MissingOptionException(nameof(options.WriteAheadLogOptions));

    if (options.DiskSegmentOptions == null)
      return new MissingOptionException(nameof(options.DiskSegmentOptions));

    Exception exception = null;
    if (mutableSegmentBloomFilterEnabled)
    {
      exception = ValidateCommonKeyHasherCompatibility(options);
      if (exception != null)
        return exception;
    }

    exception = ValidateDefinedEnum(
        nameof(options.BTreeLockMode),
        options.BTreeLockMode);
    if (exception != null)
      return exception;

    exception = ValidateWriteAheadLogOptions(options.WriteAheadLogOptions);
    if (exception != null)
      return exception;

    exception = ValidateDiskSegmentOptions(options.DiskSegmentOptions);
    if (exception != null)
      return exception;

    if (options.AllowUnsafeOptionValues)
      return null;

    return ValidateOptionValues(options);
  }

  static InvalidOptionValueException ValidateCommonKeyHasherCompatibility<TKey, TValue>(
      ZoneTreeOptions<TKey, TValue> options)
  {
    if (typeof(TKey) != typeof(string))
      return null;

    var lowerCase = (TKey)(object)"a";
    var upperCase = (TKey)(object)"A";
    if (options.Comparer.Compare(in lowerCase, in upperCase) != 0)
      return null;

    var lowerCaseHash = options.KeyHasher.GetHashCode(in lowerCase);
    var upperCaseHash = options.KeyHasher.GetHashCode(in upperCase);
    if (lowerCaseHash == upperCaseHash)
      return null;

    return new InvalidOptionValueException(
        nameof(options.KeyHasher),
        options.KeyHasher.GetType().Name,
        "Keys considered equal by Comparer must produce the same hash code");
  }

  static Exception ValidateWriteAheadLogOptions(WriteAheadLogOptions options)
  {
    const string owner = nameof(ZoneTreeOptions<object, object>.WriteAheadLogOptions);

    if (options.SyncCompressedModeOptions == null)
      return new MissingOptionException(Option(owner, nameof(options.SyncCompressedModeOptions)));

    if (options.AsyncCompressedModeOptions == null)
      return new MissingOptionException(Option(owner, nameof(options.AsyncCompressedModeOptions)));

    Exception exception = ValidateDefinedEnum(
        Option(owner, nameof(options.WriteAheadLogMode)),
        options.WriteAheadLogMode);
    if (exception != null)
      return exception;

    exception = ValidateCompressionOptions(
        owner,
        options.CompressionMethod,
        options.CompressionLevel);
    if (exception != null)
      return exception;

    return null;
  }

  static Exception ValidateDiskSegmentOptions(DiskSegmentOptions options)
  {
    const string owner = nameof(ZoneTreeOptions<object, object>.DiskSegmentOptions);

    Exception exception = ValidateDefinedEnum(
        Option(owner, nameof(options.DiskSegmentMode)),
        options.DiskSegmentMode);
    if (exception != null)
      return exception;

    exception = ValidateCompressionOptions(
        owner,
        options.CompressionMethod,
        options.CompressionLevel);
    if (exception != null)
      return exception;

    return null;
  }

  static Exception ValidateOptionValues<TKey, TValue>(
      ZoneTreeOptions<TKey, TValue> options)
  {
    Exception exception = ZoneTreeOptionValidationRules.MutableSegmentMaxItemCount.Validate(
        nameof(options.MutableSegmentMaxItemCount),
        options.MutableSegmentMaxItemCount);
    if (exception != null)
      return exception;

    exception = ZoneTreeOptionValidationRules.MutableSegmentBloomFilterBitsPerItem.Validate(
        nameof(options.MutableSegmentBloomFilterBitsPerItem),
        options.MutableSegmentBloomFilterBitsPerItem);
    if (exception != null)
      return exception;

    exception = ZoneTreeOptionValidationRules.DiskSegmentMaxItemCount.Validate(
        nameof(options.DiskSegmentMaxItemCount),
        options.DiskSegmentMaxItemCount);
    if (exception != null)
      return exception;

    exception = ZoneTreeOptionValidationRules.BTreeNodeSize.Validate(
        nameof(options.BTreeNodeSize),
        options.BTreeNodeSize);
    if (exception != null)
      return exception;

    exception = ZoneTreeOptionValidationRules.BTreeLeafSize.Validate(
        nameof(options.BTreeLeafSize),
        options.BTreeLeafSize);
    if (exception != null)
      return exception;

    exception = ValidateWriteAheadLogOptionValues(options.WriteAheadLogOptions);
    if (exception != null)
      return exception;

    return ValidateDiskSegmentOptionValues(options.DiskSegmentOptions);
  }

  static Exception ValidateWriteAheadLogOptionValues(WriteAheadLogOptions options)
  {
    const string owner = nameof(ZoneTreeOptions<object, object>.WriteAheadLogOptions);

    Exception exception = ZoneTreeOptionValidationRules.WriteAheadLogCompressionBlockSize.Validate(
        Option(owner, nameof(options.CompressionBlockSize)),
        options.CompressionBlockSize);
    if (exception != null)
      return exception;

    exception = ZoneTreeOptionValidationRules.WriteAheadLogEmptyQueuePollInterval.Validate(
        Option(owner, $"{nameof(options.AsyncCompressedModeOptions)}.{nameof(options.AsyncCompressedModeOptions.EmptyQueuePollInterval)}"),
        options.AsyncCompressedModeOptions.EmptyQueuePollInterval);
    if (exception != null)
      return exception;

    return ZoneTreeOptionValidationRules.WriteAheadLogTailWriterJobInterval.Validate(
        Option(owner, $"{nameof(options.SyncCompressedModeOptions)}.{nameof(options.SyncCompressedModeOptions.TailWriterJobInterval)}"),
        options.SyncCompressedModeOptions.TailWriterJobInterval);
  }

  static Exception ValidateDiskSegmentOptionValues(DiskSegmentOptions options)
  {
    const string owner = nameof(ZoneTreeOptions<object, object>.DiskSegmentOptions);

    Exception exception = ZoneTreeOptionValidationRules.DiskSegmentCompressionBlockSize.Validate(
        Option(owner, nameof(options.CompressionBlockSize)),
        options.CompressionBlockSize);
    if (exception != null)
      return exception;

    exception = ZoneTreeOptionValidationRules.DiskSegmentMaximumRecordCount.Validate(
        Option(owner, nameof(options.MaximumRecordCount)),
        options.MaximumRecordCount);
    if (exception != null)
      return exception;

    exception = ZoneTreeOptionValidationRules.DiskSegmentMinimumRecordCount.Validate(
        Option(owner, nameof(options.MinimumRecordCount)),
        options.MinimumRecordCount);
    if (exception != null)
      return exception;

    if (options.MinimumRecordCount >= options.MaximumRecordCount)
    {
      return new InvalidOptionValueException(
          Option(owner, nameof(options.MinimumRecordCount)),
          options.MinimumRecordCount,
          $"{nameof(options.MinimumRecordCount)} must be less than {nameof(options.MaximumRecordCount)}");
    }

    exception = ZoneTreeOptionValidationRules.DiskSegmentKeyCacheSize.Validate(
        Option(owner, nameof(options.KeyCacheSize)),
        options.KeyCacheSize);
    if (exception != null)
      return exception;

    exception = ZoneTreeOptionValidationRules.DiskSegmentValueCacheSize.Validate(
        Option(owner, nameof(options.ValueCacheSize)),
        options.ValueCacheSize);
    if (exception != null)
      return exception;

    exception = ZoneTreeOptionValidationRules.DiskSegmentMaterializedEntryCacheSize.Validate(
        Option(owner, nameof(options.MaterializedEntryCacheSize)),
        options.MaterializedEntryCacheSize);
    if (exception != null)
      return exception;

    exception = ZoneTreeOptionValidationRules.DiskSegmentSearchHintPrefetchSize.Validate(
        Option(owner, nameof(options.SearchHintPrefetchSize)),
        options.SearchHintPrefetchSize);
    if (exception != null)
      return exception;

    exception = ZoneTreeOptionValidationRules.DiskSegmentKeyCacheRecordLifeTimeInMillisecond.Validate(
        Option(owner, nameof(options.KeyCacheRecordLifeTimeInMillisecond)),
        options.KeyCacheRecordLifeTimeInMillisecond);
    if (exception != null)
      return exception;

    exception = ZoneTreeOptionValidationRules.DiskSegmentValueCacheRecordLifeTimeInMillisecond.Validate(
        Option(owner, nameof(options.ValueCacheRecordLifeTimeInMillisecond)),
        options.ValueCacheRecordLifeTimeInMillisecond);
    if (exception != null)
      return exception;

    return ZoneTreeOptionValidationRules.DiskSegmentDefaultSparseArrayStepSize.Validate(
        Option(owner, nameof(options.DefaultSparseArrayStepSize)),
        options.DefaultSparseArrayStepSize);
  }

  static Exception ValidateCompressionOptions(
      string owner,
      CompressionMethod method,
      int level)
  {
    var methodOption = Option(owner, nameof(WriteAheadLogOptions.CompressionMethod));
    Exception exception = ValidateDefinedEnum(methodOption, method);
    if (exception != null)
      return exception;

    return ValidateCompressionLevel(owner, method, level);
  }

  static CompressionLevelIsOutOfRangeException ValidateCompressionLevel(
      string option,
      CompressionMethod method,
      int level)
  {
    var exception = new CompressionLevelIsOutOfRangeException(
        option,
        method,
        level);
    return CompressionLevels.IsValid(method, level) ? null : exception;
  }

  static InvalidOptionValueException ValidateDefinedEnum<TEnum>(
      string option,
      TEnum value)
      where TEnum : struct, Enum
  {
    return Enum.IsDefined(value)
        ? null
        : new InvalidOptionValueException(
            option,
            value,
            $"Value must be a defined {typeof(TEnum).Name}");
  }

  static string Option(string owner, string property)
  {
    return owner + "." + property;
  }
}
