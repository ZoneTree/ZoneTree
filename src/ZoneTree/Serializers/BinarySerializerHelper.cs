using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ZoneTree.Serializers;

public static class BinarySerializerHelper
{
  /// <summary>
  /// Writes the value's bytes, as <see cref="ToByteArray{T}(in T)"/> returns them, at the start of the destination
  /// and returns how many it wrote.
  /// </summary>
  /// <exception cref="ArgumentException">The destination is shorter than the value.</exception>
  public static int Write<T>(in T value, Span<byte> destination) where T : unmanaged
  {
    var size = Unsafe.SizeOf<T>();
    if (destination.Length < size)
      throw new ArgumentException($"A {typeof(T)} takes {size} bytes.", nameof(destination));
    Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value);
    return size;
  }

  public static unsafe byte[] ToByteArray<T>(in T value) where T : unmanaged
  {
    byte[] result = new byte[sizeof(T)];
    Unsafe.As<byte, T>(ref result[0]) = value;
    return result;
  }

  public static T FromByteArray<T>(byte[] data) where T : unmanaged
      => Unsafe.As<byte, T>(ref data[0]);

  public static T FromByteArray<T>(byte[] data, int off) where T : unmanaged
      => Unsafe.As<byte, T>(ref data[off]);

  public static T FromByteArray<T>(Memory<byte> data) where T : unmanaged
      => Unsafe.As<byte, T>(ref data.Span[0]);

  public static T FromByteArray<T>(Memory<byte> data, int off) where T : unmanaged
      => Unsafe.As<byte, T>(ref data.Span[off]);

}
