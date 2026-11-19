using ZoneTree.Core;

namespace ZoneTree.Exceptions;

public sealed class ExistingDatabaseVersionIsNotCompatibleException : ZoneTreeException
{
  public ExistingDatabaseVersionIsNotCompatibleException(Version dbVersion, Version currentVersion)
      : base($"The database has storage format version {dbVersion?.ToString() ?? "none"}, " +
          $"and ZoneTree {currentVersion} opens databases from storage format version {ZoneTreeInfo.MinimumDbVersion} on. " +
          $"Open the database once with ZoneTree {ZoneTreeInfo.MinimumDbVersion} and ZoneTreeFactory.EnableZstdMigration() to migrate it, " +
          $"and then open it with ZoneTree {currentVersion}.")
  {
    DbVersion = dbVersion;
    CurrentVersion = currentVersion;
  }

  public Version DbVersion { get; }

  public Version CurrentVersion { get; }
}
