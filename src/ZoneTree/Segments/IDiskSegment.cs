using ZoneTree.Collections;
using ZoneTree.Segments.Disk;

namespace ZoneTree.Segments;

public interface IDiskSegment<TKey, TValue> : IReadOnlySegment<TKey, TValue>, IIndexedReader<TKey, TValue>, IDisposable
{
  /// <summary>
  /// Gets number of the records in the segment.
  /// </summary>
  new long Length { get; }

  /// <summary>
  /// Gets the count of read buffers.
  /// </summary>
  int ReadBufferCount { get; }

  /// <summary>
  /// Initializes the sparse array with the specified size.
  /// </summary>
  /// <param name="size">The size of the sparse array to initialize.</param>
  void InitSparseArray(int size);

  /// <summary>
  /// Initialize a sparse array alligned with segment length,
  /// enabling faster reads without I/O operations.
  /// </summary>
  void LoadIntoMemory();

  /// <summary>
  /// Acquires an iterator lease
  /// to ensure that the disk segment stays alive
  /// until all iterators call DetachIterator.
  /// </summary>
  void AttachIterator();

  /// <summary>
  /// Releases an iterator lease. The final lease completes a pending drop.
  /// </summary>
  void DetachIterator();

  /// <summary>
  /// Releases internal read buffers that have not been used since the specified tick count.
  /// </summary>
  /// <returns>The total number of released read buffers.</returns>
  int ReleaseReadBuffers(long ticks);

  /// <summary>
  /// Releases expired circular cache key records.
  /// </summary>
  /// <returns>The total number of released cached records.</returns>
  int ReleaseKeyCacheRecords();

  /// <summary>
  /// Releases expired circular cache value records.
  /// </summary>
  /// <returns>The total number of released cached records.</returns>
  int ReleaseValueCacheRecords();

  /// <summary>
  /// Returns the first keys of every part.
  /// </summary>
  /// <returns>An array of the first keys of each part.</returns>
  TKey[] GetFirstKeysOfEveryPart();

  /// <summary>
  /// Returns the last keys of every part.
  /// </summary>
  /// <returns>An array of the last keys of each part.</returns>
  TKey[] GetLastKeysOfEveryPart();

  /// <summary>
  /// Returns the last values of every part.
  /// </summary>
  /// <returns>An array of the last values of each part.</returns>
  TValue[] GetLastValuesOfEveryPart();

  /// <summary>
  /// Exceptions occurs in delayed drops (eg: iterators delays segment drops)
  /// are being reported to the IZoneTreeMaintenance interface events
  /// through this delegate.
  /// This is for internal usage.
  /// </summary>
  internal Action<IDiskSegment<TKey, TValue>, Exception> DropFailureReporter { get; set; }

  /// <summary>
  /// Retrieves the part of the segment at the specified index.
  /// </summary>
  /// <param name="partIndex">The index of the part to retrieve.</param>
  /// <returns>The part of the segment at the specified index.</returns>
  IDiskSegment<TKey, TValue> GetPart(int partIndex);

  /// <summary>
  /// Returns the total number of parts in the segment.
  /// </summary>
  /// <returns>The total number of parts.</returns>
  int GetPartCount();

  /// <summary>
  /// Returns all physical files that belong to this disk segment.
  /// Multipart segments include their descriptor and all part files.
  /// </summary>
  DiskSegmentFile[] GetFiles();

  /// <summary>
  /// Drops all sectors of the segment except those in the specified exclusion list.
  /// </summary>
  /// <param name="excludedPartIds">A set of part IDs to exclude from dropping.</param>
  void Drop(HashSet<long> excludedPartIds);

  /// <summary>
  /// Sets default sparse array of the disk segment and persists it to the disk.
  /// </summary>
  /// <param name="defaultSparseArray"></param>
  void SetDefaultSparseArray(IReadOnlyList<SparseArrayEntry<TKey, TValue>> defaultSparseArray);

  /// <summary>
  /// Returns a seekable iterator over the segment.
  /// </summary>
  ISeekableIterator<TKey, TValue> GetSeekableIterator(
      bool contributeToTheBlockCache,
      int prefetchSize);
}
