using ZoneTree.PresetTypes;

namespace ZoneTree.Comparers;

/// <summary>Compares the values of an enum type in ascending order of their underlying values.</summary>
/// <typeparam name="TEnum">An enum type.</typeparam>
public sealed class EnumComparerAscending<TEnum> : IRefComparer<TEnum>
{
  /// <exception cref="ArgumentException">The type is not an enum type.</exception>
  public EnumComparerAscending()
  {
    EnumTypes.Validate<TEnum>();
  }

  // Called directly, the default comparer is one the JIT resolves for the enum type and inlines.
  public int Compare(in TEnum x, in TEnum y)
    => Comparer<TEnum>.Default.Compare(x, y);
}
