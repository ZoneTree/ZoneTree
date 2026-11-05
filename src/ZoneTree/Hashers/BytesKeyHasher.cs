using System.Runtime.CompilerServices;

namespace ZoneTree.Hashers;

/// <summary>
/// Computes content-based hash codes for byte arrays. A null array hashes as an empty one.
/// </summary>
public sealed class BytesKeyHasher : IKeyHasher<byte[]>
{
  /// <inheritdoc/>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int GetHashCode(in byte[] key)
  {
    const uint offsetBasis = 2166136261;
    const uint prime = 16777619;

    var hash = offsetBasis;
    foreach (var value in key.AsSpan())
    {
      hash ^= value;
      hash *= prime;
    }
    return unchecked((int)hash);
  }
}
