using System.Buffers.Binary;
using ZoneTree.Transactional;

namespace ZoneTree.WalSoak;

// The oracle never uses database reads to calculate expected writes.
sealed class LaneModel(CaseConfig config, int lane)
{
  readonly long[] balances = Enumerable.Repeat(1_000_000L, config.Options.Accounts).ToArray();
  public long Sequence { get; private set; }
  public Dictionary<long, byte[]> Records { get; } = new();
  public static long Key(int lane, int kind, int index = 0) => ((long)(lane + 1) << 48) | ((long)kind << 32) | (uint)index;

  public void Initialize()
  {
    Records.Add(Key(lane, 0), Value(Key(lane, 0), 0, config.Seed));
    for (var i = 0; i < balances.Length; ++i)
      for (var kind = 1; kind <= 2; ++kind)
        Records.Add(Key(lane, kind, i), Value(Key(lane, kind, i), 0, balances[i]));
  }

  public Dictionary<long, byte[]?> Advance()
  {
    var seq = ++Sequence;
    var changes = new Dictionary<long, byte[]?>();
    void Put(int kind, int index, long amount, int size = 0)
    {
      var key = Key(lane, kind, index);
      changes[key] = Value(key, seq, amount, size);
    }
    var transfers = seq % 97 == 0 ? 16 : 1;
    for (var j = 0; j < transfers; ++j)
    {
      var choice = Mix((ulong)config.Seed ^ (ulong)lane << 40 ^ (ulong)seq * 8191 ^ (uint)j);
      var from = (int)(choice % (uint)balances.Length);
      var to = (from + 1 + (int)((choice >> 32) % (uint)(balances.Length - 1))) % balances.Length;
      var amount = (long)(choice % 101 + 1);
      balances[from] -= amount;
      balances[to] += amount;
      Put(1, from, balances[from]); Put(2, from, balances[from]);
      Put(1, to, balances[to]); Put(2, to, balances[to]);
      var slot = (int)((choice >> 16) % (uint)config.Options.Slots);
      for (var kind = 3; kind <= 4; ++kind)
      {
        if (seq % 5 == 0) changes[Key(lane, kind, slot)] = null;
        else Put(kind, slot, unchecked((long)choice), seq % 257 == 0 ? 2 * config.Profile.BlockSize + 17 : 32 + (int)(choice % 384));
      }
    }
    Put(0, 0, unchecked((long)Mix((ulong)seq ^ (uint)config.Seed ^ (ulong)lane << 32)));
    foreach (var (key, value) in changes)
      if (value == null) Records.Remove(key); else Records[key] = value;
    return changes;
  }

  public void AdvanceTo(long seq)
  {
    Require(seq >= Sequence && seq <= config.Options.MaxSequence + 1L, $"Invalid lane {lane} sequence {seq}; previous {Sequence}.");
    while (Sequence < seq) Advance();
  }

  static byte[] Value(long key, long seq, long amount, int payload = 0)
  {
    var value = new byte[24 + payload];
    BinaryPrimitives.WriteInt64LittleEndian(value, key);
    BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(8), seq);
    BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(16), amount);
    var state = Mix((ulong)key ^ (ulong)seq ^ (ulong)amount);
    for (var i = 24; i < value.Length; ++i)
    {
      // Alternate compressible and incompressible records.
      if ((seq & 1) != 0) state = Mix(state);
      value[i] = (byte)state;
    }
    return value;
  }

  static ulong Mix(ulong value)
  {
    value += 0x9e3779b97f4a7c15UL;
    value = (value ^ value >> 30) * 0xbf58476d1ce4e5b9UL;
    value = (value ^ value >> 27) * 0x94d049bb133111ebUL;
    return value ^ value >> 31;
  }

  public static LaneModel[] Create(CaseConfig config) => Enumerable.Range(0, config.Lanes).Select(i =>
  {
    var model = new LaneModel(config, i); model.Initialize(); return model;
  }).ToArray();

  public static void Write(ITransaction<long, byte[]> transaction, IEnumerable<KeyValuePair<long, byte[]?>> changes)
  {
    foreach (var (key, value) in changes)
      if (value == null) transaction.Delete(key); else transaction.Upsert(key, value);
  }

  public static long[] Check(IReadTransaction<long, byte[]> snapshot, CaseConfig config, LaneModel[] models, long[]? floor = null, long[]? ceiling = null)
  {
    var rows = snapshot.Range(long.MinValue, long.MaxValue).ToDictionary(x => x.Key, x => x.Value);
    return CheckRows(rows, config, models, floor, ceiling);
  }

  public static long[] CheckRows(Dictionary<long, byte[]> rows, CaseConfig config, LaneModel[] models, long[]? floor = null, long[]? ceiling = null)
  {
    var seqs = new long[config.Lanes];
    var count = 0;
    for (var lane = 0; lane < config.Lanes; ++lane)
    {
      Require(rows.TryGetValue(Key(lane, 0), out var head) && head.Length == 24, $"Missing or corrupt lane {lane} head.");
      var seq = BinaryPrimitives.ReadInt64LittleEndian(head!.AsSpan(8));
      Require(seq >= (floor?[lane] ?? 0) && seq <= (ceiling?[lane] ?? config.Options.MaxSequence + 1L), $"Lane {lane}: recovered {seq}, required [{floor?[lane] ?? 0}, {ceiling?[lane] ?? config.Options.MaxSequence + 1L}].");
      models[lane].AdvanceTo(seq);
      foreach (var (key, value) in models[lane].Records)
        Require(rows.TryGetValue(key, out var actual) && value.AsSpan().SequenceEqual(actual), $"Lane {lane}, sequence {seq}, key {key}: missing, partial, or incorrect record.");
      count += models[lane].Records.Count;
      seqs[lane] = seq;
    }
    Require(rows.Count == count, $"Found {rows.Count} keys; expected {count}. A deletion or rollback may have recovered incorrectly.");
    return seqs;
  }

  public static void SelfCheck(CaseConfig config)
  {
    var models = Create(config);
    foreach (var model in models) for (var i = 0; i < 10; ++i) model.Advance();
    var rows = models.SelectMany(x => x.Records).ToDictionary(x => x.Key, x => x.Value.ToArray());
    CheckRows(rows, config, Create(config));
    void MustReject(Action corrupt)
    {
      var copy = rows.ToDictionary(x => x.Key, x => x.Value.ToArray());
      corrupt();
      var rejected = false;
      try { CheckRows(rows, config, Create(config)); } catch (InvalidDataException) { rejected = true; }
      Require(rejected, "Oracle self-check failed to reject corruption.");
      rows = copy;
    }
    MustReject(() => rows[Key(0, 2, 0)][16] ^= 1);
    MustReject(() => rows.Remove(Key(0, 1, 0)));
    var failed = false;
    try { CheckRows(rows, config, Create(config), Enumerable.Repeat(11L, config.Lanes).ToArray()); } catch (InvalidDataException) { failed = true; }
    Require(failed, "Oracle accepted loss of a durable receipt.");
  }

  public static void Require(bool condition, string text) { if (!condition) throw new InvalidDataException(text); }
}
