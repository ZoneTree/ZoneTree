using System.Diagnostics;
using System.Reflection;

namespace ZoneTree.Core;

public static class ZoneTreeInfo
{
  static Version Version;

  /// <summary>
  /// The storage format version of the databases that this version writes.
  /// It changes only with a version that requires migrating existing databases.
  /// </summary>
  public static readonly Version DbVersion = new(2, 1, 10);

  /// <summary>
  /// Gets ZoneTree Product Version
  /// </summary>
  /// <returns></returns>
  public static Version ProductVersion
  {
    get
    {
      if (Version != null)
        return Version;
      Version = Assembly.GetExecutingAssembly().GetName().Version;
      return Version;
    }
  }
}
