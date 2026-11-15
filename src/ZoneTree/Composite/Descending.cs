namespace ZoneTree.Composite;

/// <summary>
/// A composite key element that sorts in descending order, such as the time of a newest-first index. It is
/// written as <see cref="CompositeKey.Descending{T}(T)"/> and reads back as a value of its own type.
/// </summary>
/// <typeparam name="T">A type composite key elements support.</typeparam>
public readonly record struct Descending<T>(T Value) : IDescendingElement
{
  ElementShape IDescendingElement.Inner => ShapeOf<T>.Value;
}
