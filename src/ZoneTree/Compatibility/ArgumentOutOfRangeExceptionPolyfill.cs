#if NET7_0 || NET6_0
using System.Runtime.CompilerServices;

namespace ZoneTree.AbstractFileStream
{
internal sealed class ArgumentOutOfRangeException
    : global::System.ArgumentOutOfRangeException
{
  public ArgumentOutOfRangeException(string paramName)
      : base(paramName)
  {
  }

  public ArgumentOutOfRangeException(
      string paramName,
      object actualValue,
      string message)
      : base(paramName, actualValue, message)
  {
  }

  public static void ThrowIfNegative(
      int value,
      [CallerArgumentExpression(nameof(value))] string paramName = null)
  {
    if (value < 0)
      throw new ArgumentOutOfRangeException(paramName, value, null);
  }

  public static void ThrowIfNegative(
      long value,
      [CallerArgumentExpression(nameof(value))] string paramName = null)
  {
    if (value < 0)
      throw new ArgumentOutOfRangeException(paramName, value, null);
  }

  public static void ThrowIfLessThanOrEqual(
      TimeSpan value,
      TimeSpan other,
      [CallerArgumentExpression(nameof(value))] string paramName = null)
  {
    if (value <= other)
      throw new ArgumentOutOfRangeException(paramName, value, null);
  }
}
}

namespace ZoneTree.Backup
{
internal static class ArgumentOutOfRangeException
{
  public static void ThrowIfLessThanOrEqual(
      TimeSpan value,
      TimeSpan other,
      [CallerArgumentExpression(nameof(value))] string paramName = null)
  {
    if (value <= other)
      throw new global::System.ArgumentOutOfRangeException(
          paramName,
          value,
          null);
  }
}
}
#endif
