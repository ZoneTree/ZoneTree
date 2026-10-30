namespace ZoneTree.Exceptions;

/// <summary>
/// Thrown when a transactional tree is opened with a different comparer or serializer
/// type than the one it was created with. The stored keys and values would be read
/// with another order or encoding.
/// </summary>
public sealed class TransactionalTreeSchemaMismatchException : ZoneTreeException
{
  public TransactionalTreeSchemaMismatchException(string component, string givenType)
      : base($"The transactional tree was created with a different {component}.\r\n" +
             $"Value in Runtime: {givenType}\r\n" +
             $"Open the tree with the {component} type it was created with.")
  {
    Component = component;
    GivenType = givenType;
  }

  public string Component { get; }

  public string GivenType { get; }
}
