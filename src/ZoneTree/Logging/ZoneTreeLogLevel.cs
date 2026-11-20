namespace ZoneTree.Logging;

/// <summary>
/// The least severe entry a logger writes: each level writes its own entries and those of the levels after it.
/// </summary>
public enum ZoneTreeLogLevel
{
  /// <summary>Every entry, with the steps of what runs.</summary>
  Trace,

  /// <summary>Information, warnings and errors.</summary>
  Info,

  /// <summary>Warnings and errors.</summary>
  Warning,

  /// <summary>Errors only.</summary>
  Error,

  /// <summary>Nothing.</summary>
  None
}
