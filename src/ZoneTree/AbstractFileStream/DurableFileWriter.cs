using System.Text;

namespace ZoneTree.AbstractFileStream;

/// <summary>
/// Writes whole files so that a crash never leaves a partly written file: after a crash, the path has either the
/// previous file or the complete new one.
/// </summary>
/// <remarks>
/// Each write goes first to a temporary file at the target's path with <c>.tmp</c> added, and that file is flushed to
/// disk. The temporary file then replaces the target with <see cref="IFileStreamProvider.Replace"/>, or, for a new
/// file, is renamed to the target with <see cref="IFileStreamProvider.MoveFile"/>. The methods whose names end in
/// <c>WithBackup</c> also keep the previous file under a backup name.
/// <para>
/// The new file's content is on disk when a write returns, but its name may not be yet: the name reaches the disk only
/// when the caller syncs the directory with <see cref="IFileStreamProvider.SyncDirectory"/>. This lets the caller
/// choose when that happens, for example once before it deletes an older copy it no longer needs. On the local disk on
/// Windows, where the directory sync does nothing, a write without a backup writes its rename to disk before it
/// returns.
/// </para>
/// <para>
/// All writes to a path use the same temporary file, so two writes to the same path must never run at the same time.
/// </para>
/// <para>
/// For a write whose new name must be on disk when it returns, or for writes to one path that may run at the same
/// time, use the operations from <see cref="IFileStreamProvider.GetDurableFileOperations"/> instead.
/// </para>
/// </remarks>
public sealed class DurableFileWriter
{
  const string TempFileExtension = ".tmp";

  const string BackupFileExtension = ".backup";

  readonly IFileStreamProvider FileStreamProvider;

  public DurableFileWriter(IFileStreamProvider fileStreamProvider)
  {
    FileStreamProvider = fileStreamProvider;
  }

  public string ReadAllTextFromBackup(
      string path)
  {
    var backupPath = path + BackupFileExtension;
    return FileStreamProvider.ReadAllText(backupPath);
  }

  public byte[] ReadAllBytesFromBackup(string path)
  {
    var backupPath = path + BackupFileExtension;
    return FileStreamProvider.ReadAllBytes(backupPath);
  }

  public void WriteAllTextWithBackup(string path, string text)
  {
    var tempPath = path + TempFileExtension;
    var backupPath = path + BackupFileExtension;
    if (FileStreamProvider.FileExists(backupPath))
      FileStreamProvider.DeleteFile(backupPath);
    WriteTextInternal(text, tempPath);

    FileStreamProvider.Replace(tempPath, path, backupPath);
  }

  public string ReadAllText(string file)
  {
    return FileStreamProvider.ReadAllText(file);
  }

  public byte[] ReadAllBytes(string file)
  {
    return FileStreamProvider.ReadAllBytes(file);
  }

  public void WriteAllBytesWithBackup(string path, byte[] bytes)
  {
    var tempPath = path + TempFileExtension;
    var backupPath = path + BackupFileExtension;
    if (FileStreamProvider.FileExists(backupPath))
      FileStreamProvider.DeleteFile(backupPath);
    WriteBytesInternal(bytes, tempPath);
    FileStreamProvider.Replace(tempPath, path, backupPath);
  }

  public void WriteAllText(string path, string text)
  {
    var tempPath = path + TempFileExtension;
    WriteTextInternal(text, tempPath);
    FileStreamProvider.Replace(tempPath, path, null);
  }

  public void WriteAllBytes(string path, byte[] bytes)
  {
    var tempPath = path + TempFileExtension;
    WriteBytesInternal(bytes, tempPath);
    FileStreamProvider.Replace(tempPath, path, null);
  }

  /// <summary>
  /// Creates a file that must not exist yet.
  /// A crash leaves the whole file or no file.
  /// </summary>
  public void WriteNewFile(string path, byte[] bytes)
  {
    if (FileStreamProvider.FileExists(path))
      throw new IOException($"File {path} already exists.");
    var tempPath = path + TempFileExtension;
    WriteBytesInternal(bytes, tempPath);
    FileStreamProvider.MoveFile(tempPath, path);
  }

  void WriteBytesInternal(byte[] bytes, string path)
  {
    // A power loss can leave a longer temporary file of an earlier write.
    using var tempFile = FileStreamProvider.CreateFileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);
    tempFile.Write(bytes, 0, bytes.Length);
    tempFile.Flush(true);
    tempFile.Close();
  }

  void WriteTextInternal(string text, string path)
  {
    var bytes = Encoding.UTF8.GetBytes(text);
    WriteBytesInternal(bytes, path);
  }
}
