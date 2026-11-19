using ZoneTree.Segments.Disk;

namespace ZoneTree.Backup;

/// <summary>
/// Receives live backup generation operations.
/// Implement this interface to write backup files to local disk, object
/// storage, a remote service, or any other destination.
/// </summary>
/// <remarks>
/// Segment file operations may be called concurrently for the same generation
/// when live backup is configured with more than one concurrent file transfer.
/// Implementations must make UseSegmentAsync and UploadSegmentFileAsync
/// thread-safe for a generation.
/// </remarks>
public interface ILiveBackupStore
{
  /// <summary>
  /// Allocates the next backup generation id.
  /// </summary>
  Task<long> GetNextGenerationIdAsync(CancellationToken cancellationToken);

  /// <summary>
  /// Called when a new backup generation starts.
  /// Persist treeKind in the catalog so restore can validate its storage layout.
  /// </summary>
  Task BeginGenerationAsync(
      long generationId,
      DateTime startedAtUtc,
      LiveBackupTreeKind treeKind,
      CancellationToken cancellationToken);

  /// <summary>
  /// Adds an immutable disk segment to the current generation.
  /// Returns true when ZoneTree must upload this segment file.
  /// </summary>
  Task<bool> UseSegmentAsync(
      long generationId,
      DiskSegmentFile file,
      CancellationToken cancellationToken);

  /// <summary>
  /// Uploads one immutable disk segment file.
  /// </summary>
  Task UploadSegmentFileAsync(
      long generationId,
      DiskSegmentFile file,
      Stream source,
      CancellationToken cancellationToken);

  /// <summary>
  /// Opens a writer for one logical in-memory record batch.
  /// </summary>
  Task<ILiveBackupRecordWriter> OpenRecordWriterAsync(
      long generationId,
      LiveBackupRecordBatch batch,
      CancellationToken cancellationToken);

  /// <summary>
  /// Called when the generation is complete.
  /// </summary>
  /// <param name="generationId">The generation.</param>
  /// <param name="lastOpIndex">The last op index of the tree.</param>
  /// <param name="dbVersion">
  /// The storage format version of the tree, see <see cref="Core.ZoneTreeMeta.DbVersion"/>,
  /// or null when the tree has none.
  /// </param>
  /// <param name="cancellationToken">The cancellation token.</param>
  Task CompleteGenerationAsync(
      long generationId,
      long lastOpIndex,
      string dbVersion,
      CancellationToken cancellationToken);
}
