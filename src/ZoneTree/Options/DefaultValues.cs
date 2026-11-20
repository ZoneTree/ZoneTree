using System.Collections.ObjectModel;
using ZoneTree.Collections;

namespace ZoneTree.Options;

public static class DefaultValues
{
  public static readonly int MutableSegmentMaxItemCount = 1_000_000;

  public static readonly int MutableSegmentBloomFilterBitsPerItem = 8;

  public static readonly int DiskSegmentMaxItemCount = 20_000_000;

  public static readonly BPlusTreeLockMode BPlusTreeLockMode = BPlusTreeLockMode.NodeLevelMonitor;

  public static readonly int BPlusTreeNodeSize = 128;

  public static readonly int BPlusTreeLeafSize = 128;

  public static readonly int LockStripeCount = 1024;
}
