using ZoneTree.Exceptions;

namespace ZoneTree.Backup;

public class LiveBackupException : ZoneTreeException
{
  public LiveBackupException(
      string message,
      Exception innerException = null)
      : base(message, innerException)
  {
  }
}

public sealed class LiveBackupGenerationException : LiveBackupException
{
  public LiveBackupGenerationException(
      long generationId,
      string operation,
      Exception innerException)
      : base(
          $"Live backup generation {generationId} failed while {operation}.",
          innerException)
  {
    GenerationId = generationId;
    Operation = operation;
  }

  public long GenerationId { get; }

  public string Operation { get; }
}

public sealed class LiveBackupRestoreTargetAlreadyExistsException
    : LiveBackupException
{
  public LiveBackupRestoreTargetAlreadyExistsException()
      : base(
          "Live backup restore target already contains a ZoneTree database. " +
          "Restore into an empty data directory or open the existing database.")
  {
  }
}

/// <summary>A backup file or its metadata failed integrity validation.</summary>
public sealed class LiveBackupIntegrityException : LiveBackupException
{
  public LiveBackupIntegrityException(string file, string detail)
      : this(file, detail, null)
  {
  }

  public LiveBackupIntegrityException(string file, string detail, Exception innerException)
      : base($"Live backup integrity check failed for '{file}': {detail}", innerException)
  {
    FileName = file;
  }

  public string FileName { get; }
}

/// <summary>The backup is of a database that this version does not open.</summary>
public sealed class LiveBackupVersionIsNotCompatibleException : LiveBackupException
{
  public LiveBackupVersionIsNotCompatibleException(long generationId, string dbVersion)
      : base($"Live backup generation {generationId} is of a database with storage format version " +
          $"{dbVersion ?? "none"}, and ZoneTree {Core.ZoneTreeInfo.ProductVersion} restores backups of databases " +
          $"from storage format version {Core.ZoneTreeInfo.MinimumDbVersion} on. Restore it with ZoneTree " +
          $"{Core.ZoneTreeInfo.MinimumDbVersion}, open the restored database once with " +
          "ZoneTreeFactory.EnableZstdMigration(), and take a new backup.")
  {
    GenerationId = generationId;
    DbVersion = dbVersion;
  }

  public long GenerationId { get; }

  public string DbVersion { get; }
}
