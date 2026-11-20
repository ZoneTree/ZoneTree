namespace ZoneTree.Collections;

/// <summary>
/// Available BPlusTree lock modes.
/// </summary>
/// <remarks>
/// The 3M shuffled parallel inserts and reads test results per BPlusTree lock mode.
/// The best lock mode might change depend on your hardware, data, key length etc.
/// 
/// |                Method |    Mean | Error |     Min |     Max |  Median |      Gen 0 |     Gen 1 |     Gen 2 | Allocated |
/// |---------------------- |--------:|------:|--------:|--------:|--------:|-----------:|----------:|----------:|----------:|
/// |       TopLevelMonitor | 4.318 s |    NA | 4.318 s | 4.318 s | 4.318 s | 12000.0000 | 7000.0000 | 2000.0000 |     71 MB |
/// |  TopLevelReaderWriter | 5.811 s |    NA | 5.811 s | 5.811 s | 5.811 s | 10000.0000 | 5000.0000 |         - |     71 MB |
/// |      NodeLevelMonitor | 2.067 s |    NA | 2.067 s | 2.067 s | 2.067 s | 12000.0000 | 7000.0000 | 2000.0000 |     71 MB |
/// | NodeLevelReaderWriter | 3.608 s |    NA | 3.608 s | 3.608 s | 3.608 s | 10000.0000 | 5000.0000 |         - |     74 MB |
/// </remarks>
public enum BPlusTreeLockMode
{
  /// <summary>
  /// There is no locking at all.
  /// This mode is not thread-safe.
  /// Use it only if you are going to insert and read from single thread.
  /// </summary>
  NoLock,

  /// <summary>
  /// Top level monitor lock.
  /// </summary>
  TopLevelMonitor,

  /// <summary>
  /// Top level reader-writer lock.
  /// </summary>
  TopLevelReaderWriter,

  /// <summary>
  /// Nodel level monitor lock.
  /// </summary>
  NodeLevelMonitor,

  /// <summary>
  /// Node level reader-writer lock.
  /// </summary>
  NodeLevelReaderWriter
}
