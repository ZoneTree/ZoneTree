using ZoneTree.AbstractFileStream;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Segments.RandomAccess;

namespace ZoneTree.UnitTests;

/// <summary>
/// Tests a compressed device written in appends of varying sizes and read after it is sealed. The device fills one
/// block buffer and compresses into one buffer for every block, so each block must still come back with its own
/// bytes. The lengths cover a device that ends exactly at a block boundary, one that ends inside a block, one byte,
/// and nothing.
/// </summary>
public sealed class CompressedFileRandomAccessDeviceTests
{
  const int BlockSize = 4096;

  [Test]
  public void EveryBlockReadsBackItsOwnBytesAfterSealing(
      [Values] CompressionMethod method,
      [Values(0, 1, BlockSize - 1, BlockSize, BlockSize + 1, 3 * BlockSize, 3 * BlockSize + 7)] int length)
  {
    var files = new InMemoryFileStreamProvider();
    files.CreateDirectory("devices");
    var manager = new RandomAccessDeviceManager(new ConsoleLogger(LogLevel.Error), files, "devices");
    // Bytes that compress and differ from block to block, so a block holding another block's bytes is caught.
    var random = new Random(length);
    var expected = new byte[length];
    for (var i = 0; i < length; ++i)
      expected[i] = (byte)(i / 97 + random.Next(4));

    var device = manager.CreateWritableDevice(1, ".data", isCompressed: true, BlockSize,
        deleteIfExists: true, backupIfDelete: false, method, compressionLevel: 0);
    for (var offset = 0; offset < length;)
    {
      var count = Math.Min(length - offset, random.Next(1, 1500));
      Assert.That(device.AppendBytesReturnPosition(expected.AsSpan(offset, count)), Is.EqualTo(offset));
      offset += count;
    }
    device.SealDevice();
    Assert.Multiple(() =>
    {
      Assert.That(device.Length, Is.EqualTo(length));
      Assert.That(device.GetBytes(0, length).ToArray(), Is.EqualTo(expected));
    });
    device.Close();

    var reopened = manager.GetReadOnlyDevice(1, ".data", isCompressed: true, BlockSize, method, compressionLevel: 0);
    Assert.Multiple(() =>
    {
      Assert.That(reopened.Length, Is.EqualTo(length));
      Assert.That(reopened.GetBytes(0, length).ToArray(), Is.EqualTo(expected));
    });
    manager.CloseAllDevices();
  }
}
