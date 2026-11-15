using System.Runtime.CompilerServices;

namespace ZoneTree.Hashers;

/// <summary>
/// Computes content-based hash codes for read-only byte sequences.
/// </summary>
public sealed class ReadOnlyByteMemoryKeyHasher : IKeyHasher<ReadOnlyMemory<byte>>
{
  /// <inheritdoc/>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int GetHashCode(in ReadOnlyMemory<byte> key)
  {
    const uint offsetBasis = 2166136261;
    const uint prime = 16777619;

    var hash = offsetBasis;
    foreach (var value in key.Span)
    {
      hash ^= value;
      hash *= prime;
    }
    return unchecked((int)hash);
  }
}
