using ZoneTree.Comparers;
using ZoneTree.PresetTypes;

namespace ZoneTree.UnitTests;

public sealed class ByteArrayComparerTests
{
  [Test]
  public void DefaultMemoryComparerIsLexicographicAscending()
  {
    Assert.That(ComponentsForKnownTypes.GetComparer<Memory<byte>>(),
        Is.TypeOf<ByteArrayLexicographicComparerAscending>());
  }

  [Test]
  public void LexicographicComparersOrderPrefixesAndReverseEachOther()
  {
    var ascending = new ByteArrayLexicographicComparerAscending();
    var descending = new ByteArrayLexicographicComparerDescending();
    // Listed in lexicographic order, including empty keys and byte boundaries.
    Memory<byte>[] ordered =
    [
      Memory<byte>.Empty,
      new byte[] { 0 },
      new byte[] { 0, 0 },
      new byte[] { 0, 1 },
      new byte[] { 1 },
      new byte[] { 1, 0 },
      new byte[] { 1, 0, 255 },
      new byte[] { 1, 1 },
      new byte[] { 255 },
      new byte[] { 255, 255 }
    ];

    for (var i = 0; i < ordered.Length; ++i)
    {
      for (var j = 0; j < ordered.Length; ++j)
      {
        Assert.That(Math.Sign(ascending.Compare(ordered[i], ordered[j])),
            Is.EqualTo(Math.Sign(i - j)), $"Ascending pair {i}, {j}");
        Assert.That(Math.Sign(descending.Compare(ordered[i], ordered[j])),
            Is.EqualTo(Math.Sign(j - i)), $"Descending pair {i}, {j}");
      }
    }
  }

  [Test]
  public void LexicographicComparersCompareContentsOfSlices()
  {
    Memory<byte> slice = new byte[] { 99, 1, 2, 99 }.AsMemory(1, 2);
    Memory<byte> same = new byte[] { 1, 2 };
    Memory<byte> longer = new byte[] { 1, 2, 3 };
    var ascending = new ByteArrayLexicographicComparerAscending();
    var descending = new ByteArrayLexicographicComparerDescending();

    Assert.That(ascending.Compare(slice, same), Is.Zero);
    Assert.That(descending.Compare(slice, same), Is.Zero);
    Assert.That(ascending.Compare(slice, longer), Is.LessThan(0));
    Assert.That(descending.Compare(slice, longer), Is.GreaterThan(0));
  }
}
