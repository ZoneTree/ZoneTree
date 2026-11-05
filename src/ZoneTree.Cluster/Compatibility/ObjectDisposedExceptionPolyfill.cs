#if NET6_0
namespace ZoneTree.Cluster
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
