namespace ZoneTree.Transactional.Conflicts;

/// <summary>
/// An inclusive key range. A missing bound is unbounded.
/// </summary>
internal struct KeyRange<TKey>
{
  public TKey Low;

  public bool HasLow;

  public TKey High;

  public bool HasHigh;
}
