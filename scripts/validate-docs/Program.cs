using System.Text.Json;
using System.Text.RegularExpressions;

var repositoryRoot = FindRepositoryRoot(args);
var errors = new List<string>();

var markdownFiles = GetMarkdownFiles(repositoryRoot, errors);
foreach (var markdownFile in markdownFiles)
  ValidateMarkdownLinks(repositoryRoot, markdownFile, errors);

var navigationFiles = Directory
  .EnumerateFiles(
    Path.Combine(repositoryRoot, "zonetree-dev", "content", "docs"),
    "_nav.json",
    SearchOption.AllDirectories)
  .Order(StringComparer.Ordinal)
  .ToArray();

foreach (var navigationFile in navigationFiles)
  ValidateNavigation(repositoryRoot, navigationFile, errors);

if (errors.Count != 0)
{
  foreach (var error in errors)
    Console.Error.WriteLine(error);
  return 1;
}

Console.WriteLine(
  $"Validated {markdownFiles.Count} Markdown files and {navigationFiles.Length} navigation files.");
return 0;

static string FindRepositoryRoot(string[] args)
{
  if (args.Length > 1)
    throw new ArgumentException("Usage: ValidateDocs [repository-root]");

  if (args.Length == 1)
    return Path.GetFullPath(args[0]);

  for (var directory = new DirectoryInfo(Environment.CurrentDirectory);
       directory != null;
       directory = directory.Parent)
  {
    if (File.Exists(Path.Combine(directory.FullName, "README.md")) &&
        Directory.Exists(Path.Combine(directory.FullName, "zonetree-dev", "content", "docs")))
      return directory.FullName;
  }

  throw new DirectoryNotFoundException(
    "Could not find the repository root. Pass it as the first argument.");
}

static List<string> GetMarkdownFiles(string repositoryRoot, List<string> errors)
{
  var files = new List<string>();
  AddRequiredFile(Path.Combine(repositoryRoot, "README.md"));

  var docsDirectory = Path.Combine(repositoryRoot, "zonetree-dev", "content", "docs");
  if (Directory.Exists(docsDirectory))
  {
    files.AddRange(Directory.EnumerateFiles(
      docsDirectory,
      "*.md",
      SearchOption.AllDirectories));
  }
  else
  {
    errors.Add("Missing documentation directory: zonetree-dev/content/docs");
  }

  AddRequiredFile(Path.Combine(
    repositoryRoot,
    "src",
    "ZoneTree",
    "docs",
    "ZoneTree",
    "README-NUGET.md"));

  files.Sort(StringComparer.Ordinal);
  return files;

  void AddRequiredFile(string path)
  {
    if (File.Exists(path))
      files.Add(path);
    else
      errors.Add($"Missing documentation file: {RelativePath(repositoryRoot, path)}");
  }
}

static void ValidateMarkdownLinks(
  string repositoryRoot,
  string markdownFile,
  List<string> errors)
{
  var content = File.ReadAllText(markdownFile);
  foreach (Match match in MarkdownLinkPattern().Matches(content))
  {
    var target = match.Groups["target"].Value.Trim();
    if (target.StartsWith('<') && target.EndsWith('>'))
      target = target[1..^1];

    if (IsExternalOrDocumentAnchor(target))
      continue;

    var fragmentIndex = target.IndexOf('#');
    var pathPart = fragmentIndex < 0 ? target : target[..fragmentIndex];
    if (string.IsNullOrWhiteSpace(pathPart))
      continue;

    try
    {
      pathPart = Uri.UnescapeDataString(pathPart)
        .Replace('/', Path.DirectorySeparatorChar);
      var containingDirectory = Path.GetDirectoryName(markdownFile)!;
      var resolvedPath = Path.GetFullPath(Path.Combine(containingDirectory, pathPart));
      if (!Path.Exists(resolvedPath))
      {
        errors.Add(
          $"Broken local link in {RelativePath(repositoryRoot, markdownFile)}: {target}");
      }
    }
    catch (Exception exception) when (
      exception is ArgumentException or UriFormatException or NotSupportedException)
    {
      errors.Add(
        $"Invalid local link in {RelativePath(repositoryRoot, markdownFile)}: " +
        $"{target} ({exception.Message})");
    }
  }
}

static bool IsExternalOrDocumentAnchor(string target) =>
  target.StartsWith('#') ||
  target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
  target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
  target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
  target.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

static void ValidateNavigation(
  string repositoryRoot,
  string navigationFile,
  List<string> errors)
{
  JsonDocument navigation;
  try
  {
    navigation = JsonDocument.Parse(File.ReadAllText(navigationFile));
  }
  catch (JsonException exception)
  {
    errors.Add(
      $"Invalid JSON in {RelativePath(repositoryRoot, navigationFile)}: {exception.Message}");
    return;
  }

  using (navigation)
  {
    // Navigation sequences are intentionally tolerant of missing targets. The
    // docs renderer ignores absent entries, so CI should only require valid JSON.
  }
}

static string RelativePath(string repositoryRoot, string path) =>
  Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');

static Regex MarkdownLinkPattern() => MarkdownPatterns.Link;

static class MarkdownPatterns
{
  internal static readonly Regex Link = new(
    @"!?(?:\[[^\]]*\])\((?<target>[^)]+)\)",
    RegexOptions.CultureInvariant);
}
