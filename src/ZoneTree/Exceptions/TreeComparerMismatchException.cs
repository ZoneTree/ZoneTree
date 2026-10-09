namespace ZoneTree.Exceptions;

public sealed class TreeComparerMismatchException : ZoneTreeException
{
  public TreeComparerMismatchException(string expectedComparerType, string givenComparerType)
      : base($"Tree comparer does not match.\r\nValue in metadata (JSON): {expectedComparerType}\r\nValue in Runtime: {givenComparerType}\r\n" +
             "If comparer ordering changed, export the data with the original comparer and rebuild the database. " +
             "Edit the metadata JSON only when a class rename preserves exactly the same ordering.")
  {
    ExpectedComparerType = expectedComparerType;
    GivenComparerType = givenComparerType;
  }

  public string ExpectedComparerType { get; }
  public string GivenComparerType { get; }
}
