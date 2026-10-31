namespace ZoneTree.Comparers;

/// <summary>
/// Defines a method that a type implements to compare two objects.
/// </summary>
/// <typeparam name="TKey">Key type</typeparam>
public interface IRefComparer<TKey>
{
  /// <summary>
  /// Compares two objects and returns a value indicating
  /// whether one is less than, equal to, or greater than the other.
  /// </summary>
  /// <remarks>
  /// Reads do not lock the B-tree, so this method can see a key that a concurrent
  /// write is moving, which is torn if its type is larger than a machine word. A
  /// read validates its result and retries if the key changed, and an exception
  /// thrown for such a key is ignored. So this method must not have side effects
  /// and must not access memory unsafely based on the fields of a key.
  /// </remarks>
  /// <param name="x">The first key</param>
  /// <param name="y">The second key</param>
  /// <returns>-1 (x &lt; y), 0 (x == y) or 1 (x &gt; y)</returns>    
  int Compare(in TKey x, in TKey y);
}
