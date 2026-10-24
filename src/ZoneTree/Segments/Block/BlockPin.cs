namespace ZoneTree.Segments.Block;

public sealed class BlockPin(DecompressedBlock device1 = null, DecompressedBlock device2 = null)
{
  public DecompressedBlock Device1 = device1;

  public DecompressedBlock Device2 = device2;

  public bool ContributeToTheBlockCache;

  SingleBlockPin Pin1;

  SingleBlockPin Pin2;

  /// <summary>
  /// Returns the single pin for a device. A block pin belongs to one reader,
  /// which converts it for every read, so the same single pin instance is
  /// returned and refreshed on each call instead of allocating a new one.
  /// </summary>
  public SingleBlockPin ToSingleBlockPin(int num)
  {
    if (num == 1) return Refresh(ref Pin1, Device1);
    if (num == 2) return Refresh(ref Pin2, Device2);
    throw new ArgumentException("Supported device numbers are 1 and 2 but given " + num);
  }

  SingleBlockPin Refresh(ref SingleBlockPin pin, DecompressedBlock device)
  {
    pin ??= new SingleBlockPin(device, ContributeToTheBlockCache);
    pin.Device = device;
    pin.ContributeToTheBlockCache = ContributeToTheBlockCache;
    return pin;
  }

  public void SetDevice1(DecompressedBlock device)
  {
    Device1 = device;
  }

  public void SetDevice2(DecompressedBlock device)
  {
    Device2 = device;
  }

  public void Clear()
  {
    SetDevice1(null);
    SetDevice2(null);
    // The reused single pins would otherwise keep their last blocks alive.
    if (Pin1 != null)
      Pin1.Device = null;
    if (Pin2 != null)
      Pin2.Device = null;
  }
}

