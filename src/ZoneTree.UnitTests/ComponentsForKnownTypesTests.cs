using ZoneTree.PresetTypes;

namespace ZoneTree.UnitTests;

public sealed class ComponentsForKnownTypesTests
{
  [Test]
  public void MemoryDeletionPredicateRecognizesEveryEmptyRepresentation()
  {
    var isDeleted = ComponentsForKnownTypes.GetIsDeleted<int, Memory<byte>>();
    Memory<byte>[] emptyValues =
    [
      default,
      Memory<byte>.Empty,
      new byte[0],
      new byte[] { 1, 2, 3 }.AsMemory(1, 0)
    ];

    foreach (var value in emptyValues)
      Assert.That(isDeleted(1, value), Is.True);

    Memory<byte> nonEmpty = new byte[] { 1, 2, 3 }.AsMemory(1, 1);
    Assert.That(isDeleted(1, nonEmpty), Is.False);
  }

  [Test]
  public void StructContainingReferenceUsesValueEqualityForDeletion()
  {
    var isDeleted = ComponentsForKnownTypes.GetIsDeleted<int, ValueWithReference>();
    ValueWithReference deleted = default;
    var live = new ValueWithReference("live");

    Assert.That(isDeleted(1, deleted), Is.True);
    Assert.That(isDeleted(1, live), Is.False);
  }

  [Test]
  public void NullableValueUsesNullAsDeletionMarker()
  {
    var isDeleted = ComponentsForKnownTypes.GetIsDeleted<int, int?>();

    Assert.That(isDeleted(1, null), Is.True);
    Assert.That(isDeleted(1, 0), Is.False);
    Assert.That(isDeleted(1, 1), Is.False);
  }

  [Test]
  public void ReferenceTypeOnlyTreatsNullAsDeletedEvenWithCustomEquality()
  {
    var isDeleted = ComponentsForKnownTypes.GetIsDeleted<int, EqualsNull>();

    Assert.That(isDeleted(1, null), Is.True);
    Assert.That(isDeleted(1, new EqualsNull()), Is.False);
  }

  readonly record struct ValueWithReference(string Text);

  sealed class EqualsNull
  {
    public override bool Equals(object obj) => obj is null || obj is EqualsNull;

    public override int GetHashCode() => 0;
  }
}
