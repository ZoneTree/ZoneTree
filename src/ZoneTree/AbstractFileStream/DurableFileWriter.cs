using System.Text;

namespace ZoneTree.AbstractFileStream;

/// <summary>
/// Prevents partial write (e.g. power cut)
/// by writing to a temporary file first.
/// As a final step the following options are supported:
///
/// 1. Replacement with taking a backup:
/// It replaces the target file with the temp file using
/// OS Replace API.
/// Replace method also does a backup of the target file first.
/// If the file is corrupted the backup file
/// contains the latest content prior to the crash.
/// https://source.dot.net/#System.Private.CoreLib/FileSystem.Unix.cs
/// 
/// See more details: https://github.com/dotnet/runtime/issues/18034
/// 
/// 2. Replacement with ReplaceAPI but without a backup:
/// Renaming a file in the same volume is atomic operation.
/// In this mode Replace call uses
/// Interop.Sys.Rename(sourceFullPath, destFullPath)
/// internally.
/// </summary>
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
