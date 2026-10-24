using ZoneTree.Options;
using ZoneTree.Segments.Block;

namespace ZoneTree.UnitTests;

public sealed class BlockPinTests
{
  [Test]
  public void ClearReleasesTheBlocksOfReusedSinglePins()
  {
    var block1 = CreateBlock(1);
    var block2 = CreateBlock(2);
    var blockPin = new BlockPin(block1, block2);
    var pin1 = blockPin.ToSingleBlockPin(1);
    var pin2 = blockPin.ToSingleBlockPin(2);
    Assert.That(pin1.Device, Is.SameAs(block1));
    Assert.That(pin2.Device, Is.SameAs(block2));

    blockPin.Clear();

    Assert.Multiple(() =>
    {
      Assert.That(pin1.Device, Is.Null);
      Assert.That(pin2.Device, Is.Null);
      Assert.That(blockPin.ToSingleBlockPin(1).Device, Is.Null);
      Assert.That(blockPin.ToSingleBlockPin(2).Device, Is.Null);
    });
  }

  [Test]
  public void SinglePinsFollowTheBlockPin()
  {
    var blockPin = new BlockPin { ContributeToTheBlockCache = true };
    var pin1 = blockPin.ToSingleBlockPin(1);
    Assert.That(pin1.Device, Is.Null);
    Assert.That(pin1.ContributeToTheBlockCache, Is.True);

    var block = CreateBlock(3);
    blockPin.SetDevice1(block);
    blockPin.ContributeToTheBlockCache = false;
    var refreshed = blockPin.ToSingleBlockPin(1);

    Assert.That(refreshed.Device, Is.SameAs(block));
    Assert.That(refreshed.ContributeToTheBlockCache, Is.False);
    Assert.Throws<ArgumentException>(() => blockPin.ToSingleBlockPin(3));
  }

  static DecompressedBlock CreateBlock(int blockIndex)
  {
    return new DecompressedBlock(
        blockIndex,
        Memory<byte>.Empty,
        CompressionMethod.None,
        0);
  }
}
