using ZoneTree.Exceptions;

namespace ZoneTree.Options;

internal readonly record struct Rule(
    int? MinimumValue,
    int? MaximumValue)
{
  public static Rule Min(int minimum)
  {
    return new Rule(minimum, null);
  }

  public static Rule MinMB(int megabytes)
  {
    return Min(ToBytes(megabytes));
  }

  public static Rule MinKB(int kilobytes)
  {
    return Min(ToBytesFromKilobytes(kilobytes));
  }

  public Rule Max(int maximum)
  {
    return new Rule(MinimumValue, maximum).ValidateDefinition();
  }

  public Rule MaxMB(int megabytes)
  {
    return Max(ToBytes(megabytes));
  }

  public Rule MaxKB(int kilobytes)
  {
    return Max(ToBytesFromKilobytes(kilobytes));
  }

  public InvalidOptionValueException Validate(
      string option,
      int value)
  {
    if (MinimumValue.HasValue && value < MinimumValue.Value)
    {
      return new InvalidOptionValueException(
          option,
          value,
          $"Value must be greater than or equal to {MinimumValue.Value}");
    }

    if (MaximumValue.HasValue && value > MaximumValue.Value)
    {
      return new InvalidOptionValueException(
          option,
          value,
          $"Value must be less than or equal to {MaximumValue.Value}");
    }

    return null;
  }

  Rule ValidateDefinition()
  {
    if (MinimumValue.HasValue &&
        MaximumValue.HasValue &&
        MinimumValue.Value > MaximumValue.Value)
    {
      throw new ArgumentException("Minimum cannot be greater than maximum.");
    }

    return this;
  }

  static int ToBytes(int megabytes)
  {
    return checked(megabytes * 1024 * 1024);
  }

  static int ToBytesFromKilobytes(int kilobytes)
  {
    return checked(kilobytes * 1024);
  }
}
