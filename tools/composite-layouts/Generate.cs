// Generates the typed CompositeKeyLayout classes, of one to eight elements: their constructors, Create, Range,
// Between, BeginsWith, the one-sided ranges, Decode and TryDecode. It writes CompositeKeyLayout.Typed.cs, the
// whole file. Run it from anywhere: dotnet run tools/composite-layouts/Generate.cs
using System.Runtime.CompilerServices;
using System.Text;

var names = new[] { "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth" };
var counts = new[] { "one element", "two elements", "three elements", "four elements", "five elements", "six elements",
  "seven elements", "eight elements" };
const int Width = 127;
var text = new StringBuilder();

void Line(string line = "") => text.Append(line).Append('\n');

// A member written on one line when it fits, then with its body on the next, then with its parameters too.
void Member(string head, string parameters, string body)
{
  var signature = $"  {head}({parameters})";
  if (signature.Length + body.Length + 5 <= Width)
  {
    Line($"{signature} => {body};");
    return;
  }
  if (signature.Length <= Width)
    Line(signature);
  else
  {
    Line($"  {head}(");
    Line($"      {parameters})");
  }
  Line($"      => {body};");
}

// A summary on one line when it fits, else wrapped by words.
void Summary(string sentence)
{
  var one = $"  /// <summary>{sentence}</summary>";
  if (one.Length <= Width)
  {
    Line(one);
    return;
  }
  Line("  /// <summary>");
  var line = "  ///";
  foreach (var word in sentence.Split(' '))
  {
    if (line.Length + 1 + word.Length > 118)
    {
      Line(line);
      line = "  ///";
    }
    line += " " + word;
  }
  Line(line);
  Line("  /// </summary>");
}

// The keys a member at a position bounds: those of the elements before it, then the position's element.
string Keys(int n, int k) => k == 0
    ? n == 1 ? "whose element is" : "whose first element is"
    : k == 1 ? "that start with an element and whose next element is" : "that start with elements and whose next element is";

string Types(int n) => string.Join(", ", Enumerable.Range(1, n).Select(i => $"T{i}"));
string Parameters(int n) => string.Join(", ", Enumerable.Range(0, n).Select(i => $"T{i + 1} {names[i]}"));
string Arguments(int n) => string.Join(", ", names.Take(n));

for (var n = 1; n <= 8; ++n)
{
  if (n > 1)
    Line();
  Line($"/// <summary>A layout of composite keys of a prefix and {counts[n - 1]}.</summary>");
  if (n == 1)
    Line("/// <typeparam name=\"T1\">The type of the element after the prefix.</typeparam>");
  if (n >= 3)
    Line("[SuppressMessage(\"Design\", \"CA1005\", Justification = \"A layout has a type for each element, as a tuple does.\")]");
  Line($"public sealed class CompositeKeyLayout<{Types(n)}> : CompositeKeyLayout");
  Line("{");

  // The constructor.
  if (n == 1)
  {
    Line("  /// <summary>A layout of keys that start with a prefix and go on with an element of the type, in the order given.</summary>");
    Line("  /// <param name=\"prefix\">The constant elements every key starts with, such as the name of an index; none for no prefix.</param>");
    Line("  /// <param name=\"first\">The order of the element after the prefix.</param>");
    Line("  /// <exception cref=\"ArgumentException\">The prefix is not composite key elements.</exception>");
    Line("  /// <exception cref=\"ArgumentOutOfRangeException\">An order is not one of <see cref=\"CompositeKeyOrder\"/>.</exception>");
    Line("  /// <exception cref=\"NotSupportedException\">Composite keys do not support the type, or it is a <see cref=\"Descending{T}\"/>.</exception>");
    Line("  public CompositeKeyLayout(ReadOnlySpan<byte> prefix, CompositeKeyOrder first = CompositeKeyOrder.Ascending)");
  }
  else
  {
    Line("  /// <summary>A layout of keys that start with a prefix and go on with an element of each type, in the orders given.</summary>");
    Line("  /// <exception cref=\"ArgumentException\">The prefix is not composite key elements.</exception>");
    Line("  /// <exception cref=\"ArgumentOutOfRangeException\">An order is not one of <see cref=\"CompositeKeyOrder\"/>.</exception>");
    Line("  /// <exception cref=\"NotSupportedException\">Composite keys do not support a type, or it is a <see cref=\"Descending{T}\"/>.</exception>");
    Line("  public CompositeKeyLayout(ReadOnlySpan<byte> prefix,");
    var orders = Enumerable.Range(0, n).Select(i => $"CompositeKeyOrder {names[i]} = CompositeKeyOrder.Ascending").ToArray();
    for (var i = 0; i < n; i += 2)
    {
      var pair = string.Join(", ", orders.Skip(i).Take(2));
      Line($"      {pair}{(i + 2 >= n ? ")" : ",")}");
    }
  }
  Line($"      : base(prefix, [{Arguments(n)}])");
  Line("  {");
  for (var i = 1; i <= n; ++i)
    Line($"    Check<T{i}>();");
  Line("  }");
  Line();

  // Create.
  Line("  /// <summary>Encodes a key of the layout.</summary>");
  Member("public byte[] Create", Parameters(n), $"Key({Arguments(n)})");


  // Range of each leading run.
  for (var k = 1; k < n; ++k)
  {
    Line();
    Line(k == 1
        ? "  /// <summary>The range of the layout's keys that start with an element.</summary>"
        : "  /// <summary>The range of the layout's keys that start with elements.</summary>");
    Member("public CompositeKeyRange Range", Parameters(k), $"RangeUnder(Key({Arguments(k)}))");
  }

  // Between at each position, after the elements before it.
  for (var k = 0; k < n; ++k)
  {
    Line();
    var keys = Keys(n, k);
    Line("  /// <summary>");
    Line($"  /// The range of the layout's keys {keys} from one value through");
    Line("  /// another, given in either order, each included or excluded as the bounds say.");
    Line("  /// </summary>");
    Line("  /// <exception cref=\"ArgumentOutOfRangeException\">The bounds are not one of <see cref=\"CompositeKeyBounds\"/>.</exception>");
    var fixedParameters = k == 0 ? "" : Parameters(k) + ", ";
    var fixedArguments = k == 0 ? "" : Arguments(k) + ", ";
    var values = $"{fixedParameters}T{k + 1} low, T{k + 1} high";
    const string boundsParameter = "CompositeKeyBounds bounds = CompositeKeyBounds.Inclusive";
    const string head = "public CompositeKeyRange Between";
    var low = $"Key({fixedArguments}low)";
    var high = $"Key({fixedArguments}high)";
    var body = $"RangeBetween({low}, {high}, bounds)";
    var signature = $"  {head}({values}, {boundsParameter})";
    if (signature.Length + body.Length + 5 <= Width)
    {
      Line($"{signature} => {body};");
      continue;
    }
    if (signature.Length <= Width)
      Line(signature);
    else if ($"      {values}, {boundsParameter})".Length <= Width)
    {
      Line($"  {head}(");
      Line($"      {values}, {boundsParameter})");
    }
    else
    {
      Line($"  {head}(");
      Line($"      {values},");
      Line($"      {boundsParameter})");
    }
    if (body.Length + 10 <= Width)
      Line($"      => {body};");
    else
      Line($"      => RangeBetween(\n          {low},\n          {high},\n          bounds);");
  }

  // BeginsWith at each position, of text and of bytes, after the elements before it.
  foreach (var (parameter, argument, begins) in new[]
  {
    ("string text", "text", "a string that begins with given text"),
    ("ReadOnlySpan<byte> bytes", "bytes", "a bytes element that begins with given bytes"),
  })
  {
    for (var k = 0; k < n; ++k)
    {
      Line();
      Summary($"The range of the layout's keys {Keys(n, k)} {begins}.");
      if (argument == "text")
      {
        Line("  /// <exception cref=\"ArgumentNullException\">The text is null.</exception>");
        Line("  /// <exception cref=\"ArgumentException\">The text is not valid UTF-16.</exception>");
        Line($"  /// <exception cref=\"PositionTypeMismatchException\">The type at position {k} is not string.</exception>");
      }
      else
        Line($"  /// <exception cref=\"PositionTypeMismatchException\">The type at position {k} is not a bytes type.</exception>");
      var fixedParameters = k == 0 ? "" : Parameters(k) + ", ";
      var elements = k == 0 ? "Prefix" : $"Key({Arguments(k)})";
      Member("public CompositeKeyRange BeginsWith", fixedParameters + parameter, $"RangeBeginningWith<T{k + 1}>({elements}, {k}, {argument})");
    }
  }

  // The one-sided ranges at each position, after the elements before it.
  foreach (var (name, end, side) in new[]
  {
    ("AtLeast", "low", "at least a low value"), ("GreaterThan", "low", "greater than a low value"),
    ("AtMost", "high", "at most a high value"), ("LessThan", "high", "less than a high value"),
  })
  {
    for (var k = 0; k < n; ++k)
    {
      Line();
      Summary($"The range of the layout's keys {Keys(n, k)} {side}.");
      var fixedParameters = k == 0 ? "" : Parameters(k) + ", ";
      var fixedArguments = k == 0 ? "" : Arguments(k) + ", ";
      Member($"public CompositeKeyRange {name}", $"{fixedParameters}T{k + 1} {end}", $"Range{name}(Key({fixedArguments}{end}))");
    }
  }

  // Decode.
  Line();
  Line("  /// <summary>Reads a key of the layout.</summary>");
  Line("  /// <exception cref=\"ArgumentException\">The key is not of the layout.</exception>");
  Line("  /// <exception cref=\"InvalidCastException\">An element has another type.</exception>");
  Line("  /// <exception cref=\"OverflowException\">A type cannot hold its element's value exactly.</exception>");
  if (n == 1)
  {
    Line("  public T1 Decode(ReadOnlySpan<byte> key)");
    Line("  {");
    Line("    var reader = Elements(key);");
    Line("    var first = Next<T1>(ref reader, 0);");
    Line("    End(ref reader);");
    Line("    return first;");
    Line("  }");
  }
  else
  {
    Line($"  public ({Types(n)}) Decode(ReadOnlySpan<byte> key)");
    Line("  {");
    Line("    var reader = Elements(key);");
    var reads = Enumerable.Range(0, n).Select(i => $"Next<T{i + 1}>(ref reader, {i})").ToArray();
    var chunks = Enumerable.Range(0, (n + 3) / 4).Select(c => string.Join(", ", reads.Skip(c * 4).Take(4)));
    Line($"    var elements = ({string.Join(",\n        ", chunks)});");
    Line("    End(ref reader);");
    Line("    return elements;");
    Line("  }");
  }

  // TryDecode.
  Line();
  Line("  /// <summary>Reads a key of the layout, as <see cref=\"Decode\"/> does, but returns false where it throws.</summary>");
  Line("  /// <returns>");
  Line("  /// False if the key is not of the layout, an element has another type, a type cannot hold its element's value");
  Line("  /// exactly, or a string element's bytes are not valid UTF-8.");
  Line("  /// </returns>");
  if (n == 1)
  {
    Line("  public bool TryDecode(ReadOnlySpan<byte> key, out T1 first)");
    Line("  {");
    Line("    if (TryElements(key, out var reader) && TryNext(ref reader, 0, out first) && TryEnd(ref reader))");
    Line("      return true;");
    Line("    first = default;");
    Line("    return false;");
    Line("  }");
  }
  else
  {
    Line($"  public bool TryDecode(ReadOnlySpan<byte> key, out ({Types(n)}) elements)");
    Line("  {");
    Line("    elements = default;");
    var steps = new List<string> { "TryElements(key, out var reader)" };
    steps.AddRange(Enumerable.Range(0, n).Select(i => $"TryNext(ref reader, {i}, out elements.Item{i + 1})"));
    steps.Add("TryEnd(ref reader)");
    var lines = Enumerable.Range(0, (steps.Count + 1) / 2).Select(c => string.Join(" && ", steps.Skip(c * 2).Take(2)));
    Line($"    if ({string.Join(" &&\n        ", lines)})");
    Line("      return true;");
    Line("    elements = default;");
    Line("    return false;");
    Line("  }");
  }
  Line("}");
}

// The file is found from this file's own path, so the generator runs from any directory.
var path = Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "..", "src", "ZoneTree", "Composite", "CompositeKeyLayout.Typed.cs"));
const string Header = """
    // Generated by tools/composite-layouts/Generate.cs. Change the generator and run it; don't edit this file.
    using System.Diagnostics.CodeAnalysis;
    using ZoneTree.Exceptions;

    namespace ZoneTree.Composite;


    """;
File.WriteAllText(path, Header + text);
Console.WriteLine($"Wrote the typed layouts to {path}.");

static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
