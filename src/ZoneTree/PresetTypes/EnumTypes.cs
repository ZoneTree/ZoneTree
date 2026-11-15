namespace ZoneTree.PresetTypes;

/// <summary>Checks the type arguments of the built-in components of enum types.</summary>
internal static class EnumTypes
{
  /// <exception cref="ArgumentException">The type is not an enum type.</exception>
  internal static void Validate<TEnum>()
  {
    if (!typeof(TEnum).IsEnum)
      throw new ArgumentException($"{typeof(TEnum)} is not an enum type.");
  }
}
