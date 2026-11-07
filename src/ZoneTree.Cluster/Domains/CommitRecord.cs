namespace ZoneTree.Cluster;

/// <summary>
/// The status records of a coordinated column, as stored in its tree and read by recovery
/// without the column's types.
/// </summary>
/// <remarks>
/// <para>
/// The commit of a transaction stores a commit record after its versions, in every column it
/// wrote. Its meta holds the commit's sequence number and the number of columns the
/// transaction wrote. A transaction is complete when every one of those columns holds its
/// commit record. A pending status record, with meta <see cref="Pending"/>, marks the versions
/// of a transaction that did not commit, where a merge or a recovery stored one.
/// </para>
/// <para>
/// The domain numbers its commits without gaps, in commit order. A commit that reached none
/// of its columns leaves its number missing, so recovery finds every lost commit, and keeps
/// the commits before the first missing or incomplete number.
/// </para>
/// <para>
/// A column also stores its durable watermark: every commit at or below that sequence number
/// is complete and durable in every column of the domain. It covers the commit records the
/// column's merges moved to disk segments, which recovery does not read.
/// </para>
/// </remarks>
internal static class CommitRecord
{
  /// <summary>The meta of a pending status record.</summary>
  internal const long Pending = 0;

  const int SequenceBits = 55;

  /// <summary>The largest sequence number a commit record holds.</summary>
  internal const long MaximumSequence = (1L << SequenceBits) - 1;

  /// <summary>The most columns one transaction writes.</summary>
  internal const int MaximumParticipants = 255;

  /// <summary>The version of the durable watermark among the clock kind records.</summary>
  internal const long DurableWatermarkVersion = 4;

  internal static long Encode(long sequence, int participants)
  {
    if ((ulong)sequence > MaximumSequence)
      throw new InvalidOperationException("The domain passed the largest commit sequence number.");
    return sequence | ((long)participants << SequenceBits);
  }

  internal static long Sequence(long meta) => meta & MaximumSequence;

  internal static int Participants(long meta) => (int)((ulong)meta >> SequenceBits);
}
