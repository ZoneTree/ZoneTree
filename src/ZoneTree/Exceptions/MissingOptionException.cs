namespace ZoneTree.Exceptions;

public sealed class MissingOptionException : ZoneTreeException
{
  public MissingOptionException(string missingOption)
      : base($"ZoneTree {missingOption} option is not provided.")
  {
    MissingOption = missingOption;
  }

  public MissingOptionException(string missingOption, string resolution)
      : base($"ZoneTree {missingOption} option is not provided. {resolution}")
  {
    MissingOption = missingOption;
  }

  public string MissingOption { get; }
}
