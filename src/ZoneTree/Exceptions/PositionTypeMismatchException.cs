using ZoneTree.Composite;

namespace ZoneTree.Exceptions;

/// <summary>
/// A composite key layout was asked for a range its position's type cannot have, such as text that an element
/// begins with at a position whose elements are integers.
/// </summary>
public sealed class PositionTypeMismatchException : ZoneTreeException
{
  public PositionTypeMismatchException(int position, Type positionType, CompositeElementType requiredElementType)
      : base($"Position {position} after the layout's prefix holds {positionType} elements, " +
             $"and the range needs {requiredElementType} elements there.")
  {
    Position = position;
    PositionType = positionType;
    RequiredElementType = requiredElementType;
  }

  /// <summary>The position after the layout's prefix, counted from zero.</summary>
  public int Position { get; }

  /// <summary>The type of the layout's elements at the position.</summary>
  public Type PositionType { get; }

  /// <summary>The element type the range needs at the position: a string for text, and bytes for bytes.</summary>
  public CompositeElementType RequiredElementType { get; }
}
