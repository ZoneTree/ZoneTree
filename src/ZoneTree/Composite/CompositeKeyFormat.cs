using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace ZoneTree.Composite;

/// <summary>
/// The byte format of composite keys. A key is its elements one after another, with no header. Each element is a
/// tag byte and a payload whose bytes sort as the element's values do. A descending element is the complement of
/// its ascending bytes, tag included. Every element's bytes are prefix-free, so a key sorts before every key that
/// extends it, and complementing an element reverses its order.
/// </summary>
internal static class CompositeKeyFormat
{
  // Ascending tags, in the order elements of different types sort. Descending tags are their complements,
  // 0x80 to 0xFE. No element starts with 0x00 or 0xFF, so 0xFF after a prefix bounds every key that extends it.
  internal const byte Null = 0x01;
  internal const byte BytesTag = 0x02;
  internal const byte StringTag = 0x03;
  // An integer's tag holds its sign and its byte count: 0x0D is zero, 0x0E to 0x15 a positive value of 1 to 8
  // bytes, and 0x0C down to 0x05 a negative one of 1 to 8 bytes. A wider integer, of 9 to 255 bytes, has a
  // tag of its own on either side and a length byte, complemented for a negative one so longer ones sort first.
  internal const byte WideNegativeInteger = 0x04;
  internal const byte IntegerZero = 0x0D;
  internal const byte WidePositiveInteger = 0x16;
  internal const byte LowestInteger = WideNegativeInteger;
  internal const byte HighestInteger = WidePositiveInteger;
  internal const int MaximumNarrowWidth = 8;
  internal const int MaximumWideWidth = 255;
  internal const byte Floating = 0x17;
  internal const byte NegativeDecimal = 0x18;
  internal const byte DecimalZero = 0x19;
  internal const byte PositiveDecimal = 0x1A;
  internal const byte False = 0x1B;
  internal const byte True = 0x1C;
  internal const byte DateTimeTag = 0x1D;
  internal const byte DateTimeOffsetTag = 0x1E;
  internal const byte DateOnlyTag = 0x1F;
  internal const byte TimeOnlyTag = 0x20;
  internal const byte TimeSpanTag = 0x21;
  internal const byte GuidTag = 0x22;

  /// <summary>The byte after a prefix that every key extending the prefix sorts before.</summary>
  internal const byte PrefixEnd = 0xFF;

  // Bytes and strings escape a zero byte as 00 FF and end with 00 01.
  internal const byte Escape = 0xFF;
  internal const byte Terminator = 0x01;

  // A decimal is a sign, an exponent and its significant digits: 0.d1d2...dn times ten to the exponent.
  internal const int DecimalExponentBias = 0x80;
  internal const int LowestDecimalExponent = -27;
  internal const int HighestDecimalExponent = 29;
  internal const int MaximumDecimalDigits = 29;

  internal const ulong SignBit = 0x8000_0000_0000_0000;
  internal const ulong CanonicalNaN = 0x7FF8_0000_0000_0000;

  internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

  /// <summary>The length of a string's UTF-8 bytes, which a writer measures before it writes them.</summary>
  /// <param name="text">The string.</param>
  /// <param name="what">How the message names the string, such as "A string element".</param>
  /// <param name="parameter">The parameter the string came in, if it is one.</param>
  /// <exception cref="ArgumentException">The string is not valid UTF-16: it holds a surrogate with no pair.</exception>
  internal static int Utf8Length(string text, string what, string parameter = null)
  {
    try
    {
      return StrictUtf8.GetByteCount(text);
    }
    catch (EncoderFallbackException error)
    {
      throw new ArgumentException($"{what} is not valid UTF-16: the surrogate at index {error.Index} has no pair.", parameter, error);
    }
  }

  /// <summary>The ascending tag of an element, and the mask its descending bytes are read through.</summary>
  internal static byte AscendingTag(byte first, out byte mask)
  {
    if (first >= 0x80)
    {
      mask = 0xFF;
      return (byte)~first;
    }
    mask = 0;
    return first;
  }

  internal static CompositeElementType TypeOf(byte tag) => tag switch
  {
    Null => CompositeElementType.Null,
    BytesTag => CompositeElementType.Bytes,
    StringTag => CompositeElementType.String,
    Floating => CompositeElementType.Floating,
    NegativeDecimal or DecimalZero or PositiveDecimal => CompositeElementType.Decimal,
    False or True => CompositeElementType.Boolean,
    DateTimeTag => CompositeElementType.DateTime,
    DateTimeOffsetTag => CompositeElementType.DateTimeOffset,
    DateOnlyTag => CompositeElementType.DateOnly,
    TimeOnlyTag => CompositeElementType.TimeOnly,
    TimeSpanTag => CompositeElementType.TimeSpan,
    GuidTag => CompositeElementType.Guid,
    _ => CompositeElementType.Integer
  };

  /// <summary>
  /// The length of the element at an offset, checked to be in canonical form, or -1 for bytes that are not one.
  /// Whether a string's bytes are valid UTF-8 is checked only as it is read.
  /// </summary>
  internal static int Measure(ReadOnlySpan<byte> key, int offset)
  {
    var first = key[offset];
    if (first == 0x00 || first == PrefixEnd)
      return -1;
    var tag = AscendingTag(first, out var mask);
    var rest = key.Slice(offset + 1);
    switch (tag)
    {
      case Null:
      case False:
      case True:
      case IntegerZero:
      case DecimalZero:
        return 1;
      case BytesTag:
      case StringTag:
        var escaped = EscapedLength(rest, mask);
        return escaped < 0 ? -1 : 1 + escaped;
      case Floating:
        return rest.Length >= 8 && IsCanonicalFloating(Read64(rest, mask)) ? 9 : -1;
      case DateTimeTag:
      case DateTimeOffsetTag:
        return rest.Length >= 8 && Read64(rest, mask) <= (ulong)DateTime.MaxValue.Ticks ? 9 : -1;
      case DateOnlyTag:
        return rest.Length >= 4 && Read32(rest, mask) <= (uint)DateOnly.MaxValue.DayNumber ? 5 : -1;
      case TimeOnlyTag:
        return rest.Length >= 8 && Read64(rest, mask) <= (ulong)TimeOnly.MaxValue.Ticks ? 9 : -1;
      case TimeSpanTag:
        return rest.Length >= 8 ? 9 : -1;
      case GuidTag:
        return rest.Length >= 16 ? 17 : -1;
      case NegativeDecimal:
      case PositiveDecimal:
        return TryReadDecimal(rest, DecimalMask(tag, mask), tag == NegativeDecimal, out _, out var length) ? 1 + length : -1;
      default:
        if (tag < LowestInteger || tag > HighestInteger)
          return -1;
        var negative = tag < IntegerZero;
        var width = IntegerWidth(tag, rest, mask, out var head);
        if (width < 0 || rest.Length < head + width)
          return -1;
        // A magnitude starts with a nonzero byte; a negative integer holds it complemented.
        var top = (byte)(rest[head] ^ mask);
        return top == (negative ? 0xFF : 0) ? -1 : 1 + head + width;
    }
  }

  /// <summary>
  /// The byte count of an integer's magnitude, and the length byte before it of a wide integer, or -1 for a wide
  /// integer whose length byte is missing or would fit a narrow one.
  /// </summary>
  internal static int IntegerWidth(byte tag, ReadOnlySpan<byte> payload, byte mask, out int head)
  {
    head = 0;
    if (tag != WideNegativeInteger && tag != WidePositiveInteger)
      return tag > IntegerZero ? tag - IntegerZero : IntegerZero - tag;
    head = 1;
    if (payload.Length == 0)
      return -1;
    var width = (byte)(payload[0] ^ mask);
    if (tag == WideNegativeInteger)
      width = (byte)~width;
    return width > MaximumNarrowWidth ? width : -1;
  }

  /// <summary>
  /// The magnitude of a wide integer, big-endian with its first byte nonzero, unmasked into a destination of
  /// <see cref="MaximumWideWidth"/> bytes or more.
  /// </summary>
  internal static ReadOnlySpan<byte> WideMagnitude(byte tag, ReadOnlySpan<byte> payload, byte mask, Span<byte> destination)
  {
    var width = IntegerWidth(tag, payload, mask, out var head);
    var magnitudeMask = tag == WideNegativeInteger ? (byte)~mask : mask;
    for (var i = 0; i < width; ++i)
      destination[i] = (byte)(payload[head + i] ^ magnitudeMask);
    return destination.Slice(0, width);
  }

  internal static uint Read32(ReadOnlySpan<byte> payload, byte mask)
  {
    var value = BinaryPrimitives.ReadUInt32BigEndian(payload);
    return mask == 0 ? value : ~value;
  }

  /// <summary>The magnitude of a negative integer from its complemented bytes.</summary>
  internal static ulong Magnitude(ReadOnlySpan<byte> payload, int width, byte mask)
  {
    var value = ReadBigEndian(payload, width, mask);
    var bits = width == 8 ? ulong.MaxValue : (1UL << (8 * width)) - 1;
    return ~value & bits;
  }

  internal static ulong ReadBigEndian(ReadOnlySpan<byte> payload, int width, byte mask)
  {
    ulong value = 0;
    for (var i = 0; i < width; ++i)
      value = (value << 8) | (byte)(payload[i] ^ mask);
    return value;
  }

  internal static ulong Read64(ReadOnlySpan<byte> payload, byte mask)
  {
    var value = BinaryPrimitives.ReadUInt64BigEndian(payload);
    return mask == 0 ? value : ~value;
  }

  /// <summary>The bits of a double whose order-preserving form these are.</summary>
  internal static ulong FloatingBits(ulong ordered)
      => (ordered & SignBit) != 0 ? ordered & ~SignBit : ~ordered;

  /// <summary>The order-preserving form of a double: one zero and one NaN, which sorts after positive infinity.</summary>
  internal static ulong OrderedFloating(double value)
  {
    ulong bits;
    if (double.IsNaN(value))
      bits = CanonicalNaN;
    else if (value == 0)
      bits = 0;
    else
      bits = (ulong)BitConverter.DoubleToInt64Bits(value);
    return (bits & SignBit) != 0 ? ~bits : bits | SignBit;
  }

  static bool IsCanonicalFloating(ulong ordered)
  {
    var bits = FloatingBits(ordered);
    if (bits == SignBit)
      return false;
    var isNaN = (bits & 0x7FF0_0000_0000_0000) == 0x7FF0_0000_0000_0000 && (bits & 0x000F_FFFF_FFFF_FFFF) != 0;
    return !isNaN || bits == CanonicalNaN;
  }

  /// <summary>The length of escaped bytes with their terminator, or -1 for bytes that are not escaped bytes.</summary>
  internal static int EscapedLength(ReadOnlySpan<byte> payload, byte mask)
  {
    var lead = mask;
    var i = 0;
    while (true)
    {
      var found = payload.Slice(i).IndexOf(lead);
      if (found < 0)
        return -1;
      i += found;
      if (i + 1 >= payload.Length)
        return -1;
      var next = (byte)(payload[i + 1] ^ mask);
      if (next == Terminator)
        return i + 2;
      if (next != Escape)
        return -1;
      i += 2;
    }
  }

  /// <summary>The length of escaped bytes once unescaped, where <paramref name="payload"/> excludes the terminator.</summary>
  internal static int UnescapedLength(ReadOnlySpan<byte> payload, byte mask)
  {
    // Each escaped zero byte is a lead byte and its escape, which is never a lead byte itself.
#if NET8_0_OR_GREATER
    return payload.Length - payload.Count(mask);
#else
    var length = payload.Length;
    var i = 0;
    while (true)
    {
      var found = payload.Slice(i).IndexOf(mask);
      if (found < 0)
        return length;
      i += found + 2;
      --length;
    }
#endif
  }

  /// <summary>Unescapes bytes into a destination of their unescaped length.</summary>
  internal static void Unescape(ReadOnlySpan<byte> payload, byte mask, Span<byte> destination)
  {
    var written = 0;
    for (var i = 0; i < payload.Length; ++i)
    {
      var value = (byte)(payload[i] ^ mask);
      destination[written++] = value;
      // A zero byte is followed by its escape.
      if (value == 0)
        ++i;
    }
  }

  internal static byte[] ReadBytes(ReadOnlySpan<byte> payload, byte mask)
  {
    var bytes = new byte[UnescapedLength(payload, mask)];
    Unescape(payload, mask, bytes);
    return bytes;
  }

  /// <summary>Decodes a string's UTF-8 bytes.</summary>
  /// <exception cref="DecoderFallbackException">The bytes are not valid UTF-8.</exception>
  internal static string ReadString(ReadOnlySpan<byte> payload, byte mask)
  {
    if (mask == 0 && payload.IndexOf((byte)0) < 0)
      return StrictUtf8.GetString(payload);
    var length = UnescapedLength(payload, mask);
    byte[] rented = null;
    var buffer = length <= 256 ? stackalloc byte[length] : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
    try
    {
      Unescape(payload, mask, buffer);
      return StrictUtf8.GetString(buffer);
    }
    finally
    {
      if (rented != null)
        ArrayPool<byte>.Shared.Return(rented);
    }
  }

  /// <summary>Decodes a string's UTF-8 bytes, unless they are not valid UTF-8.</summary>
  internal static bool TryReadString(ReadOnlySpan<byte> payload, byte mask, out string value)
  {
    if (mask == 0 && payload.IndexOf((byte)0) < 0)
      return TryDecodeText(payload, out value);
    var length = UnescapedLength(payload, mask);
    byte[] rented = null;
    var buffer = length <= 256 ? stackalloc byte[length] : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
    try
    {
      Unescape(payload, mask, buffer);
      return TryDecodeText(buffer, out value);
    }
    finally
    {
      if (rented != null)
        ArrayPool<byte>.Shared.Return(rented);
    }
  }

  /// <summary>Whether a string's bytes are valid UTF-8.</summary>
  internal static bool IsValidUtf8(ReadOnlySpan<byte> payload, byte mask)
  {
    var length = UnescapedLength(payload, mask);
    byte[] rented = null;
    var buffer = length <= 256 ? stackalloc byte[length] : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
    try
    {
      Unescape(payload, mask, buffer);
      return IsValidText(buffer);
    }
    finally
    {
      if (rented != null)
        ArrayPool<byte>.Shared.Return(rented);
    }
  }

  /// <summary>Whether bytes are valid UTF-8.</summary>
  internal static bool IsValidText(ReadOnlySpan<byte> utf8)
  {
#if NET8_0_OR_GREATER
    return System.Text.Unicode.Utf8.IsValid(utf8);
#else
    try
    {
      StrictUtf8.GetCharCount(utf8);
      return true;
    }
    catch (DecoderFallbackException)
    {
      return false;
    }
#endif
  }

  /// <summary>Decodes UTF-8 bytes, unless they are not valid UTF-8.</summary>
  internal static bool TryDecodeText(ReadOnlySpan<byte> utf8, out string value)
  {
#if NET8_0_OR_GREATER
    if (!System.Text.Unicode.Utf8.IsValid(utf8))
    {
      value = null;
      return false;
    }
    value = StrictUtf8.GetString(utf8);
    return true;
#else
    try
    {
      value = StrictUtf8.GetString(utf8);
      return true;
    }
    catch (DecoderFallbackException)
    {
      value = null;
      return false;
    }
#endif
  }

  /// <summary>The mask a decimal's payload is read through: a negative decimal holds its magnitude complemented.</summary>
  internal static byte DecimalMask(byte tag, byte mask)
      => tag == NegativeDecimal ? (byte)~mask : mask;

  /// <summary>
  /// Reads a nonzero decimal's exponent and digits, checked to be in canonical form and to fit a decimal.
  /// </summary>
  internal static bool TryReadDecimal(ReadOnlySpan<byte> payload, byte mask, bool negative, out decimal value, out int length)
  {
    value = 0;
    length = 0;
    if (payload.Length < 2)
      return false;
    var exponent = (byte)(payload[0] ^ mask) - DecimalExponentBias;
    if (exponent < LowestDecimalExponent || exponent > HighestDecimalExponent)
      return false;
    uint low = 0, middle = 0, high = 0;
    var digits = 0;
    var last = 0;
    var i = 1;
    while (true)
    {
      if (i >= payload.Length)
        return false;
      var digit = (byte)(payload[i++] ^ mask);
      if (digit == 0)
        break;
      digit -= (byte)'0';
      if (digit > 9 || (digits == 0 && digit == 0) || ++digits > MaximumDecimalDigits || !TryMultiplyAdd(ref low, ref middle, ref high, digit))
        return false;
      last = digit;
    }
    if (digits == 0 || last == 0)
      return false;
    var scale = digits - exponent;
    if (scale > 28)
      return false;
    for (; scale < 0; ++scale)
    {
      if (!TryMultiplyAdd(ref low, ref middle, ref high, 0))
        return false;
    }
    value = new decimal((int)low, (int)middle, (int)high, negative, (byte)scale);
    length = i;
    return true;
  }

  /// <summary>Multiplies a 96-bit integer by ten and adds a digit, unless the result overflows.</summary>
  static bool TryMultiplyAdd(ref uint low, ref uint middle, ref uint high, uint digit)
  {
    var result = (ulong)low * 10 + digit;
    var nextLow = (uint)result;
    result = (ulong)middle * 10 + (result >> 32);
    var nextMiddle = (uint)result;
    result = (ulong)high * 10 + (result >> 32);
    if (result >> 32 != 0)
      return false;
    low = nextLow;
    middle = nextMiddle;
    high = (uint)result;
    return true;
  }

  /// <summary>Divides a 96-bit integer by ten and returns the remainder.</summary>
  internal static byte DivideByTen(ref uint low, ref uint middle, ref uint high)
  {
    var remainder = (ulong)high;
    high = (uint)(remainder / 10);
    remainder = ((remainder % 10) << 32) | middle;
    middle = (uint)(remainder / 10);
    remainder = ((remainder % 10) << 32) | low;
    low = (uint)(remainder / 10);
    return (byte)(remainder % 10);
  }

  /// <summary>Writes a Guid's 16 bytes in the order of its text form.</summary>
  internal static void WriteGuid(Guid value, Span<byte> destination)
  {
    value.TryWriteBytes(destination);
    destination.Slice(0, 4).Reverse();
    destination.Slice(4, 2).Reverse();
    destination.Slice(6, 2).Reverse();
  }

  internal static Guid ReadGuid(ReadOnlySpan<byte> payload, byte mask)
  {
    Span<byte> bytes = stackalloc byte[16];
    for (var i = 0; i < 16; ++i)
      bytes[i] = (byte)(payload[i] ^ mask);
    bytes.Slice(0, 4).Reverse();
    bytes.Slice(4, 2).Reverse();
    bytes.Slice(6, 2).Reverse();
    return new Guid(bytes);
  }

  /// <summary>
  /// The bytes just below whole elements, which a range that excludes the elements' keys ends at: every key below
  /// the elements sorts below them, no key lies between them and the elements, and no key equals them.
  /// </summary>
  /// <remarks>
  /// Byte order has no bytes just below a key in general, as a key can go on with any run of 0xFF bytes and still
  /// sort below it. The last element's encoding gives them: after a fixed-width payload, nothing but the next
  /// element's tag can follow, and a terminated payload has a byte no other payload byte can take. The element is
  /// what the bytes sort below:
  /// <list type="bullet">
  /// <item>A fixed-width payload, its width set by its tag or by a length byte: the payload minus one, then 0xFF,
  /// after every element of a smaller payload and what follows it; for a payload of zeros, the tag and length
  /// alone, which no element of the tag sorts below.</item>
  /// <item>Escaped bytes or a string, ended by 00 01, or 0xFF 0xFE descending: the bytes before the last, ending in
  /// a lead byte no terminator or escape follows.</item>
  /// <item>A decimal's digits, ended by 0x00, or 0xFF where the digits are complemented: the digits alone, or with
  /// 0xD0, above every complemented digit and below the terminator.</item>
  /// <item>An element with no payload: the element of the tag below it with its greatest payload, then 0xFF.</item>
  /// </list>
  /// </remarks>
  internal static byte[] Below(ReadOnlySpan<byte> elements)
  {
    if (elements.Length == 0)
      return [];
    var last = LastElement(elements);
    var head = elements.Slice(0, last);
    var element = elements.Slice(last);
    var tag = AscendingTag(element[0], out var mask);
    var descending = mask != 0;
    switch (tag)
    {
      case BytesTag:
      case StringTag:
        // Ascending: 00 01 becomes 00, which a 01 or an escape must follow. Descending: FF FE becomes FF 01.
        return descending ? Join(head, element[..^1], 0x01) : Join(head, element[..^1]);
      case PositiveDecimal:
      case NegativeDecimal:
        // The digits end with 0x00 where they are as they are, and with 0xFF where they are complemented.
        return element[^1] == 0x00 ? Join(head, element[..^1]) : Join(head, element[..^1], 0xD0);
      case WideNegativeInteger:
      case WidePositiveInteger:
        // The tag and the length byte, then the magnitude.
        return BelowPayload(head, element, 2);
      case Null:
      case IntegerZero:
      case DecimalZero:
      case False:
      case True:
        return Join(head, descending ? BelowDescendingEmpty(tag) : BelowAscendingEmpty(tag));
      default:
        // An integer of 1 to 8 bytes, a floating value, a time, or a Guid: the tag, then a payload of a fixed width.
        return BelowPayload(head, element, 1);
    }
  }

  /// <summary>
  /// The bytes below an element of a fixed-width payload after a head of the given length: the payload minus one,
  /// then 0xFF, or the head alone for a payload of zeros.
  /// </summary>
  static byte[] BelowPayload(ReadOnlySpan<byte> head, ReadOnlySpan<byte> element, int elementHead)
  {
    var payload = element.Slice(elementHead);
#if NET7_0_OR_GREATER
    var position = payload.LastIndexOfAnyExcept((byte)0);
#else
    var position = payload.Length - 1;
    while (position >= 0 && payload[position] == 0)
      --position;
#endif
    if (position < 0)
      return Join(head, element.Slice(0, elementHead));
    var below = Join(head, element, PrefixEnd);
    var at = head.Length + elementHead + position;
    --below[at];
    below.AsSpan(at + 1, payload.Length - position - 1).Fill(0xFF);
    return below;
  }

  /// <summary>
  /// The bytes below an ascending element with no payload: the tag below it, then 0xFF, which none of that tag's
  /// payloads starts with.
  /// </summary>
  static byte[] BelowAscendingEmpty(byte tag) => tag switch
  {
    // No element sorts below null; 0x00 starts none.
    Null => [0x00],
    // A negative integer of 1 byte holds its magnitude complemented, whose top bit is never all ones.
    IntegerZero => [IntegerZero - 1, PrefixEnd],
    // A negative decimal starts with its exponent complemented, and a positive one with its exponent, never 0xFF.
    DecimalZero => [NegativeDecimal, PrefixEnd],
    False => [PositiveDecimal, PrefixEnd],
    _ => [False, PrefixEnd]
  };

  /// <summary>
  /// The bytes below a descending element with no payload: the descending element of the tag below it with its
  /// greatest payload, then 0xFF.
  /// </summary>
  static byte[] BelowDescendingEmpty(byte tag) => tag switch
  {
    // Below a descending null, descending bytes, whose greatest is the empty bytes, FD FF FE.
    Null => [unchecked((byte)~BytesTag), PrefixEnd, PrefixEnd],
    // Below a descending zero, a descending positive integer of 1 byte, which holds its value complemented, never 0xFF.
    IntegerZero => [unchecked((byte)~(IntegerZero + 1)), PrefixEnd],
    // Below a descending decimal zero, a descending positive decimal, which starts with its exponent complemented.
    DecimalZero => [unchecked((byte)~PositiveDecimal), PrefixEnd],
    // Below a descending false, a descending true, which has no payload.
    False => [unchecked((byte)~True), PrefixEnd],
    // Below a descending true, a descending DateTime, whose ticks complemented can be all ones.
    _ => [unchecked((byte)~DateTimeTag), PrefixEnd, PrefixEnd, PrefixEnd, PrefixEnd, PrefixEnd, PrefixEnd, PrefixEnd, PrefixEnd, PrefixEnd]
  };

  static byte[] Join(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail)
  {
    var joined = new byte[head.Length + tail.Length];
    head.CopyTo(joined);
    tail.CopyTo(joined.AsSpan(head.Length));
    return joined;
  }

  static byte[] Join(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, byte end)
  {
    var joined = new byte[head.Length + tail.Length + 1];
    head.CopyTo(joined);
    tail.CopyTo(joined.AsSpan(head.Length));
    joined[^1] = end;
    return joined;
  }

  /// <summary>Where the last of whole elements starts, or -1 for none.</summary>
  /// <exception cref="ArgumentException">The bytes are not whole elements.</exception>
  internal static int LastElement(ReadOnlySpan<byte> elements)
  {
    var last = -1;
    for (var offset = 0; offset < elements.Length;)
    {
      var length = Measure(elements, offset);
      if (length < 0)
        throw Malformed(offset);
      last = offset;
      offset += length;
    }
    return last;
  }

  /// <summary>
  /// The bytes below every element after a head, and above the head itself: the head and 0x00, which starts no
  /// element.
  /// </summary>
  internal static byte[] FirstAfter(ReadOnlySpan<byte> head) => Join(head, 0x00);

  /// <summary>
  /// An upper bound at a tag after a head, which is no key: the head and the tag, or the bytes just below the
  /// element the tag is by itself where it has no payload.
  /// </summary>
  internal static byte[] UpperAtTag(ReadOnlySpan<byte> head, byte tag)
  {
    var end = Join(head, tag);
    return Measure(end, head.Length) == 1 ? Below(end) : end;
  }

  static byte[] Join(ReadOnlySpan<byte> head, byte end) => Join(head, [], end);

  internal static ArgumentException Malformed(int offset)
      => new($"The bytes are not a composite key: the element at byte {offset} is malformed.");
}
