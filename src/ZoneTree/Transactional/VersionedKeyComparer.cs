using System.Runtime.CompilerServices;
using ZoneTree.Comparers;

namespace ZoneTree.Transactional;

/// <summary>
/// Orders versioned keys by kind, then by user key, then by version descending.
/// </summary>
/// <typeparam name="TKey">The user key type.</typeparam>
public sealed class VersionedKeyComparer<TKey> : IRefComparer<VersionedKey<TKey>>
{
  /// <summary>
  /// The user key comparer.
  /// </summary>
  public IRefComparer<TKey> KeyComparer { get; }

  /// <summary>
  /// Creates the comparer.
  /// </summary>
  /// <param name="keyComparer">The user key comparer.</param>
  public VersionedKeyComparer(IRefComparer<TKey> keyComparer)
  {
    KeyComparer = keyComparer;
  }

  /// <inheritdoc/>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int Compare(in VersionedKey<TKey> x, in VersionedKey<TKey> y)
  {
    if (x.Kind != y.Kind)
      return x.Kind < y.Kind ? -1 : 1;
    if (x.Kind == VersionedKeyKind.Data)
    {
      var result = KeyComparer.Compare(in x.Key, in y.Key);
      if (result != 0)
        return result;
    }
    return y.Version.CompareTo(x.Version);
  }
}
