using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ZoneTree.PresetTypes;

namespace ZoneTree.Serializers;

/// <summary>Serializes the values of an enum type as the bytes of their underlying values, little-endian.</summary>
/// <typeparam name="TEnum">An enum type.</typeparam>
public sealed class EnumSerializer<TEnum> : ISerializer<TEnum>
{
  /// <exception cref="ArgumentException">The type is not an enum type.</exception>
  public EnumSerializer()
  {
    EnumTypes.Validate<TEnum>();
  }

  /// <exception cref="ArgumentOutOfRangeException">The bytes are fewer than the enum type's size.</exception>
  public TEnum Deserialize(Memory<byte> bytes)
  {
    var span = bytes.Span;
    if (span.Length < Unsafe.SizeOf<TEnum>())
      throw new ArgumentOutOfRangeException(nameof(bytes), span.Length, $"A {typeof(TEnum)} takes {Unsafe.SizeOf<TEnum>()} bytes.");
    if (BitConverter.IsLittleEndian)
      return Unsafe.ReadUnaligned<TEnum>(ref MemoryMarshal.GetReference(span));
    Span<byte> swapped = stackalloc byte[Unsafe.SizeOf<TEnum>()];
    span.Slice(0, swapped.Length).CopyTo(swapped);
    swapped.Reverse();
    return Unsafe.ReadUnaligned<TEnum>(ref MemoryMarshal.GetReference(swapped));
  }

  public Memory<byte> Serialize(in TEnum entry)
  {
    var bytes = new byte[Unsafe.SizeOf<TEnum>()];
    Unsafe.WriteUnaligned(ref bytes[0], entry);
    if (!BitConverter.IsLittleEndian)
      bytes.AsSpan().Reverse();
    return bytes;
  }
}
