namespace ZoneTree.Options;

/// <summary>
/// Available compression methods.
/// </summary>
/// <remarks>
/// See <see cref="CompressionLevels"/> for supported compression levels.
/// The values are stored in the files, so they keep the numbers of earlier versions.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1008", Justification = "The files store the values.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1027", Justification = "The values are not flags.")]
public enum CompressionMethod : byte
{
  /// <summary>
  /// ZStd algorithm.
  /// </summary>
  Zstd = 1,

  /// <summary>
  /// No compression.
  /// </summary>
  None = 4,
}
