namespace ZoneTree.Composite;

/// <summary>The order a composite key element sorts in.</summary>
public enum CompositeKeyOrder
{
  /// <summary>The element sorts in the ascending order of its values.</summary>
  Ascending = 0,

  /// <summary>The element sorts in the descending order of its values, after the ascending elements of its position.</summary>
  Descending = 1
}
