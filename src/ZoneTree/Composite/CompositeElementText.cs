using System.Globalization;
using System.Numerics;
using System.Text;

namespace ZoneTree.Composite;

/// <summary>
/// The text of composite elements, the same for keys and values: each element in a form its type can be told
/// from, culture-invariant, as <see cref="CompositeKey.Format"/> describes.
/// </summary>
internal static class CompositeElementText
{
  static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

  /// <summary>Appends an element's text.</summary>
  /// <returns>False, with nothing appended, if the element cannot be read, as a string of invalid UTF-8.</returns>
  internal static bool TryAppend<TFormat>(StringBuilder text, ReadOnlySpan<byte> payload, in ElementInfo element)
      where TFormat : struct, IElementFormat
  {
    var format = default(TFormat);
    switch (element.Type)
    {
      case CompositeElementType.Null:
        text.Append("null");
        return true;
      case CompositeElementType.Bytes:
        text.Append("0x").Append(Convert.ToHexString(format.ReadBytes(payload, element)));
        return true;
      case CompositeElementType.String:
        if (!format.TryReadString(payload, element, out var value))
          return false;
        AppendQuoted(text, value);
        return true;
      case CompositeElementType.Integer:
        text.Append(CompositeElementReading.Read<BigInteger, TFormat>(payload, element).ToString(Invariant));
        return true;
      case CompositeElementType.Floating:
        string number;
        if (format.TryReadHalf(payload, element, out var half))
          number = half.ToString(Invariant);
        else if (format.TryReadSingle(payload, element, out var single))
          number = single.ToString(Invariant);
        else
          number = format.ReadFloating(payload, element).ToString(Invariant);
        text.Append(number);
        // A whole number would read as an integer.
        if (IsWholeNumber(number))
          text.Append(".0");
        return true;
      case CompositeElementType.Decimal:
        text.Append(format.ReadDecimal(payload, element).ToString(Invariant)).Append('m');
        return true;
      case CompositeElementType.Boolean:
        text.Append(format.ReadBoolean(payload, element) ? "true" : "false");
        return true;
      case CompositeElementType.DateTime:
        AppendDateTime(text, format.ReadDateTime(payload, element));
        return true;
      case CompositeElementType.DateTimeOffset:
        text.Append(format.ReadDateTimeOffset(payload, element).ToString("O", Invariant));
        return true;
      case CompositeElementType.DateOnly:
        text.Append(format.ReadDateOnly(payload, element).ToString("O", Invariant));
        return true;
      case CompositeElementType.TimeOnly:
        text.Append('T').Append(format.ReadTimeOnly(payload, element).ToString("O", Invariant));
        return true;
      case CompositeElementType.TimeSpan:
        text.Append(format.ReadTimeSpan(payload, element).ToString("c", Invariant));
        return true;
      case CompositeElementType.Guid:
        text.Append(format.ReadGuid(payload, element).ToString("D", Invariant));
        return true;
      default:
        return false;
    }
  }

  /// <summary>Appends bytes that are not composite elements.</summary>
  internal static void AppendMalformed(StringBuilder text, ReadOnlySpan<byte> bytes)
      => text.Append("malformed(0x").Append(Convert.ToHexString(bytes)).Append(')');

  /// <summary>
  /// Appends a DateTime in ISO 8601: with Z for UTC, and with local after it for local time, whose offset the
  /// process's time zone would give only now.
  /// </summary>
  static void AppendDateTime(StringBuilder text, DateTime value)
  {
    text.Append(DateTime.SpecifyKind(value, DateTimeKind.Unspecified).ToString("O", Invariant));
    if (value.Kind == DateTimeKind.Utc)
      text.Append('Z');
    else if (value.Kind == DateTimeKind.Local)
      text.Append(" local");
  }

  static bool IsWholeNumber(string number)
  {
    foreach (var c in number)
    {
      if (c != '-' && c is not (>= '0' and <= '9'))
        return false;
    }
    return true;
  }

  /// <summary>Appends a string in quotes, with the escapes of C# for quotes, backslashes and control characters.</summary>
  static void AppendQuoted(StringBuilder text, string value)
  {
    text.Append('"');
    foreach (var c in value)
    {
      switch (c)
      {
        case '"':
          text.Append("\\\"");
          break;
        case '\\':
          text.Append("\\\\");
          break;
        case '\0':
          text.Append("\\0");
          break;
        case '\n':
          text.Append("\\n");
          break;
        case '\r':
          text.Append("\\r");
          break;
        case '\t':
          text.Append("\\t");
          break;
        default:
          if (char.IsControl(c))
            text.Append("\\u").Append(((int)c).ToString("X4", Invariant));
          else
            text.Append(c);
          break;
      }
    }
    text.Append('"');
  }
}
