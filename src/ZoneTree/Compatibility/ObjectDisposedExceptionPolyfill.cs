#if NET6_0
namespace ZoneTree.AbstractFileStream
{
internal static class ObjectDisposedException
{
  public static void ThrowIf(bool condition, object instance)
  {
    if (condition)
      throw new global::System.ObjectDisposedException(
          instance?.GetType().FullName);
  }
}
}

namespace ZoneTree.Backup
{
internal sealed class ObjectDisposedException
    : global::System.ObjectDisposedException
{
  public ObjectDisposedException(string objectName)
      : base(objectName)
  {
  }

  public static void ThrowIf(bool condition, object instance)
  {
    if (condition)
      throw new global::System.ObjectDisposedException(
          instance?.GetType().FullName);
  }
}
}

namespace ZoneTree.Segments.Disk
{
internal static class ObjectDisposedException
{
  public static void ThrowIf(bool condition, object instance)
  {
    if (condition)
      throw new global::System.ObjectDisposedException(
          instance?.GetType().FullName);
  }
}
}

namespace ZoneTree.Segments.MultiPart
{
internal static class ObjectDisposedException
{
  public static void ThrowIf(bool condition, object instance)
  {
    if (condition)
      throw new global::System.ObjectDisposedException(
          instance?.GetType().FullName);
  }
}
}
#endif
