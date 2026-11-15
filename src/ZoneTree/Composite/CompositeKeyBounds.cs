namespace ZoneTree.Composite;

/// <summary>
/// Which of its two values a range from one value through another includes, as
/// <see cref="CompositeKey.Between(ReadOnlySpan{byte}, ReadOnlySpan{byte}, CompositeKeyBounds)"/> and a layout's
/// Between take it. A value is included with every key under it, or excluded with every key under it.
/// </summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1008",
    Justification = "The zero value includes both values, as a range does unless told otherwise; None would not say which.")]
public enum CompositeKeyBounds
{
  /// <summary>Both values are included.</summary>
  Inclusive = 0,

  /// <summary>The low value is excluded, and the high value included.</summary>
  ExcludeLow = 1,

  /// <summary>
  /// The low value is included, and the high value excluded: a range from one value up to another, as a time
  /// window from its start up to its end.
  /// </summary>
  ExcludeHigh = 2,

  /// <summary>Both values are excluded, leaving only what lies between them.</summary>
  Exclusive = ExcludeLow | ExcludeHigh
}
