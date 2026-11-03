using ZoneTree.AbstractFileStream;
using ZoneTree.Transactional;

namespace ZoneTree.UnitTests;

/// <summary>
/// Deterministic ports of Hermitage's ten anomaly families and distinct schedules:
/// G0, G1a, G1b, G1c, OTV, PMP, P4, G-single, G2-item and G2.
/// </summary>
/// <remarks>
/// Adapted from Martin Kleppmann and contributors' Hermitage suite (CC BY 4.0):
/// https://github.com/ept/hermitage (https://creativecommons.org/licenses/by/4.0/).
/// The SQL Server and PostgreSQL schedules supply the common cases, Oracle adds
/// different G2 predicates, and FoundationDB adds a predicate-write rollback case.
///
/// Transactions are interleaved on one thread to preserve the published schedules
/// without sleeps or timing dependencies. ZoneTree takes snapshots at BeginTransaction
/// and reports optimistic conflicts at commit instead of blocking SQL statements.
/// SQL value predicates are evaluated over fully enumerated key ranges; Serializable
/// therefore protects the scanned range, including rows rejected by the predicate.
/// Snapshot permits G2-item and G2; both levels prevent the other anomaly families.
/// </remarks>
[TestFixture(TransactionIsolationLevel.Snapshot)]
[TestFixture(TransactionIsolationLevel.Serializable)]
public sealed class HermitageIsolationTests
{
  readonly TransactionIsolationLevel IsolationLevel;
  ITransactionalZoneTree<int, int> Tree;

  public HermitageIsolationTests(TransactionIsolationLevel isolationLevel)
  {
    IsolationLevel = isolationLevel;
  }

  [SetUp]
  public void SetUp()
  {
    Tree = new ZoneTreeFactory<int, int>(new InMemoryFileStreamProvider())
        .SetDataDirectory(nameof(HermitageIsolationTests))
        .OpenOrCreateTransactional();
    using var seed = Begin();
    seed.Upsert(1, 10);
    seed.Upsert(2, 20);
    Commit(seed, true);
  }

  [TearDown]
  public void TearDown()
  {
    Tree?.Dispose();
  }

  ITransaction<int, int> Begin() => Tree.BeginTransaction(IsolationLevel);

  static int Read(IReadTransaction<int, int> transaction, int key)
  {
    Assert.That(transaction.TryGet(key, out var value), Is.True, $"key {key} must exist");
    return value;
  }

  static (int Key, int Value)[] Rows(
      IReadTransaction<int, int> transaction, Func<int, bool> predicate = null)
      => transaction.Range(int.MinValue, int.MaxValue)
          .Where(row => predicate == null || predicate(row.Value))
          .Select(row => (row.Key, row.Value))
          .ToArray();

  static void Commit(ITransaction<int, int> transaction, bool expected)
  {
    Assert.That(transaction.TryCommit(), Is.EqualTo(expected),
        $"transaction {transaction.TransactionId} ({transaction.IsolationLevel}) commit");
    Assert.That(transaction.State, Is.EqualTo(
        expected ? TransactionState.Committed : TransactionState.Aborted));
  }

  void AssertCommitted(params (int Key, int Value)[] expected)
  {
    using var reader = Tree.BeginReadOnlyTransaction();
    Assert.That(Rows(reader), Is.EqualTo(expected));
  }

  static int UpdateWhere(
      ITransaction<int, int> transaction, Func<int, bool> predicate, Func<int, int> update)
  {
    // Materialize the statement's read set before modifying any of its rows.
    var rows = Rows(transaction, predicate);
    foreach (var (key, value) in rows)
      transaction.Upsert(key, update(value));
    return rows.Length;
  }

  static int DeleteWhere(ITransaction<int, int> transaction, Func<int, bool> predicate)
  {
    var rows = Rows(transaction, predicate);
    foreach (var (key, _) in rows)
      transaction.Delete(key);
    return rows.Length;
  }

  [TestCase(false)]
  [TestCase(true)]
  public void G0_WriteCyclesArePrevented(bool secondCommitsFirst)
  {
    using var t1 = Begin();
    using var t2 = Begin();
    t1.Upsert(1, 11);
    t2.Upsert(1, 12);
    t1.Upsert(2, 21);

    if (secondCommitsFirst)
    {
      t2.Upsert(2, 22);
      Commit(t2, true);
      Commit(t1, false);
      AssertCommitted((1, 12), (2, 22));
    }
    else
    {
      Commit(t1, true);
      AssertCommitted((1, 11), (2, 21));
      // Hermitage's second write resumes after T1 commits. ZoneTree rejects
      // the stale write rather than waiting and then overwriting T1.
      t2.Upsert(2, 22);
      Commit(t2, false);
      AssertCommitted((1, 11), (2, 21));
    }
  }

  [Test]
  public void G1a_AbortedReadsArePrevented()
  {
    using var t1 = Begin();
    using var t2 = Begin();
    t1.Upsert(1, 101);
    Assert.That(Read(t1, 1), Is.EqualTo(101));
    Assert.That(Read(t2, 1), Is.EqualTo(10));
    Assert.That(Rows(t2), Is.EqualTo(new[] { (1, 10), (2, 20) }));

    t1.Rollback();
    Assert.That(t1.State, Is.EqualTo(TransactionState.Aborted));
    Assert.That(Read(t2, 1), Is.EqualTo(10));
    Assert.That(Rows(t2), Is.EqualTo(new[] { (1, 10), (2, 20) }));
    Commit(t2, true);
    AssertCommitted((1, 10), (2, 20));
  }

  [Test]
  public void G1b_IntermediateReadsArePrevented()
  {
    using var t1 = Begin();
    using var t2 = Begin();
    t1.Upsert(1, 101);
    Assert.That(Read(t2, 1), Is.EqualTo(10));
    Assert.That(Rows(t2), Is.EqualTo(new[] { (1, 10), (2, 20) }));
    t1.Upsert(1, 11);
    Assert.That(Read(t1, 1), Is.EqualTo(11));
    Assert.That(Read(t2, 1), Is.EqualTo(10));
    Commit(t1, true);

    Assert.That(Read(t2, 1), Is.EqualTo(10));
    Assert.That(Rows(t2), Is.EqualTo(new[] { (1, 10), (2, 20) }));
    Commit(t2, true);
    AssertCommitted((1, 11), (2, 20));
  }

  [Test]
  public void G1c_CircularInformationFlowIsPrevented()
  {
    using var t1 = Begin();
    using var t2 = Begin();
    t1.Upsert(1, 11);
    t2.Upsert(2, 22);
    Assert.That(Read(t1, 2), Is.EqualTo(20));
    Assert.That(Read(t2, 1), Is.EqualTo(10));
    Assert.That(Rows(t1), Is.EqualTo(new[] { (1, 11), (2, 20) }));
    Assert.That(Rows(t2), Is.EqualTo(new[] { (1, 10), (2, 22) }));
    Commit(t1, true);

    // The reads avoid G1c at both levels. Committing both transactions would
    // instead form a G2-item cycle, which only Snapshot permits.
    var secondCommits = IsolationLevel == TransactionIsolationLevel.Snapshot;
    Commit(t2, secondCommits);
    AssertCommitted((1, 11), (2, secondCommits ? 22 : 20));
  }

  [Test]
  public void OTV_OverlappingWritersDoNotExposePartialTransactions()
  {
    using var t1 = Begin();
    using var t2 = Begin();
    using var t3 = Begin();
    t1.Upsert(1, 11);
    t1.Upsert(2, 19);
    t2.Upsert(1, 12);
    Commit(t1, true);

    // Unlike engines with a lazy first-read snapshot, T3's snapshot was
    // fixed when it began, so it must retain the entire initial state.
    Assert.That(Read(t3, 1), Is.EqualTo(10));
    Assert.That(Rows(t3), Is.EqualTo(new[] { (1, 10), (2, 20) }));
    t2.Upsert(2, 18);
    Assert.That(Read(t3, 2), Is.EqualTo(20));
    Commit(t2, false);
    Assert.That(Rows(t3), Is.EqualTo(new[] { (1, 10), (2, 20) }));
    Commit(t3, true);
    AssertCommitted((1, 11), (2, 19));
  }

  [Test]
  public void OTV_ObservedTransactionDoesNotVanishDuringReplacement()
  {
    using (var t1 = Begin())
    {
      t1.Upsert(1, 11);
      t1.Upsert(2, 19);
      Commit(t1, true);
    }

    // Begin after T1 to actually observe its commit, then replace its rows
    // while this observer is open. Both writers can now legitimately commit.
    using var t2 = Begin();
    using var t3 = Begin();
    t2.Upsert(1, 12);
    Assert.That(Read(t3, 1), Is.EqualTo(11));
    Assert.That(Rows(t3), Is.EqualTo(new[] { (1, 11), (2, 19) }));
    t2.Upsert(2, 18);
    Assert.That(Read(t3, 2), Is.EqualTo(19));
    Assert.That(Rows(t3), Is.EqualTo(new[] { (1, 11), (2, 19) }));
    Commit(t2, true);

    Assert.That(Read(t3, 2), Is.EqualTo(19));
    Assert.That(Read(t3, 1), Is.EqualTo(11));
    Assert.That(Rows(t3), Is.EqualTo(new[] { (1, 11), (2, 19) }));
    Commit(t3, true);
    AssertCommitted((1, 12), (2, 18));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void PMP_ReadPredicatesKeepTheirSnapshot(bool overlappingPredicates)
  {
    using var t1 = Begin();
    using var t2 = Begin();
    Assert.That(Rows(t1, value => value == 30), Is.Empty);
    t2.Upsert(3, 30);
    Commit(t2, true);

    // FoundationDB repeats the equality predicate; the other ports use
    // an overlapping divisibility predicate for the second read.
    Assert.That(Rows(t1, value => overlappingPredicates ? value % 3 == 0 : value == 30), Is.Empty);
    Commit(t1, true);
    AssertCommitted((1, 10), (2, 20), (3, 30));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void PMP_WritePredicatesRejectConcurrentUpdates(bool deleteAfterCommit)
  {
    using var t1 = Begin();
    using var t2 = Begin();
    Assert.That(UpdateWhere(t1, _ => true, value => value + 10), Is.EqualTo(2));
    Assert.That(Rows(t2), Is.EqualTo(new[] { (1, 10), (2, 20) }));

    if (deleteAfterCommit)
      Commit(t1, true);

    // The predicate selects key 2 from T2's snapshot, never key 1 from T1.
    Assert.That(Rows(t2, value => value == 20), Is.EqualTo(new[] { (2, 20) }));
    Assert.That(DeleteWhere(t2, value => value == 20), Is.EqualTo(1));

    if (!deleteAfterCommit)
    {
      Assert.That(Rows(t2), Is.EqualTo(new[] { (1, 10) }));
      Commit(t1, true);
      Assert.That(Rows(t2), Is.EqualTo(new[] { (1, 10) }));
    }

    Commit(t2, false);
    AssertCommitted((1, 20), (2, 30));
  }

  [Test]
  public void PMP_ExistingRowsKeepTheirPredicateMembership()
  {
    using var t1 = Begin();
    using var t2 = Begin();
    Assert.That(Rows(t2), Is.EqualTo(new[] { (1, 10), (2, 20) }));
    Assert.That(UpdateWhere(t1, _ => true, value => value + 10), Is.EqualTo(2));
    Commit(t1, true);

    Assert.That(Rows(t2), Is.EqualTo(new[] { (1, 10), (2, 20) }));
    Assert.That(DeleteWhere(t2, value => value == 20), Is.EqualTo(1));
    Commit(t2, false);
    AssertCommitted((1, 20), (2, 30));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void P4_LostUpdatesArePrevented(bool secondCommitsFirst)
  {
    using var t1 = Begin();
    using var t2 = Begin();
    var firstValue = Read(t1, 1);
    var secondValue = Read(t2, 1);
    Assert.That(firstValue, Is.EqualTo(10));
    Assert.That(secondValue, Is.EqualTo(10));
    t1.Upsert(1, firstValue + 1);
    t2.Upsert(1, secondValue + 1);

    Commit(secondCommitsFirst ? t2 : t1, true);
    Commit(secondCommitsFirst ? t1 : t2, false);
    AssertCommitted((1, 11), (2, 20));
  }

  [Test]
  public void GSingle_ReadOnlyTransactionDoesNotObserveReadSkew()
  {
    using var t1 = Begin();
    using var t2 = Begin();
    Assert.That(Read(t1, 1), Is.EqualTo(10));
    Assert.That(Read(t2, 1), Is.EqualTo(10));
    Assert.That(Read(t2, 2), Is.EqualTo(20));
    t2.Upsert(1, 12);
    t2.Upsert(2, 18);
    Commit(t2, true);

    Assert.That(Read(t1, 2), Is.EqualTo(20));
    Assert.That(Read(t1, 1) + Read(t1, 2), Is.EqualTo(30));
    Commit(t1, true);
    AssertCommitted((1, 12), (2, 18));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void GSingle_PredicateDependenciesDoNotExposeReadSkew(bool insert)
  {
    using var t1 = Begin();
    using var t2 = Begin();
    Assert.That(Rows(t1, value => value % 5 == 0), Is.EqualTo(new[] { (1, 10), (2, 20) }));

    // SQL Server uses an insertion; PostgreSQL and Oracle update a row
    // so that it moves from the first predicate into the second.
    if (insert)
      t2.Upsert(3, 30);
    else
      Assert.That(UpdateWhere(t2, value => value == 10, _ => 12), Is.EqualTo(1));
    Commit(t2, true);

    Assert.That(Rows(t1, value => value % 3 == 0), Is.Empty);
    Assert.That(Rows(t1, value => value % 5 == 0), Is.EqualTo(new[] { (1, 10), (2, 20) }));
    Commit(t1, true);
    if (insert)
      AssertCommitted((1, 10), (2, 20), (3, 30));
    else
      AssertCommitted((1, 12), (2, 20));
  }

  [Test]
  public void GSingle_WritePredicateCannotDeleteAnOutdatedVersion()
  {
    using var t1 = Begin();
    using var t2 = Begin();
    Assert.That(Read(t1, 1), Is.EqualTo(10));
    Assert.That(Rows(t2), Is.EqualTo(new[] { (1, 10), (2, 20) }));
    t2.Upsert(1, 12);
    t2.Upsert(2, 18);
    Commit(t2, true);

    Assert.That(DeleteWhere(t1, value => value == 20), Is.EqualTo(1));
    Commit(t1, false);
    AssertCommitted((1, 12), (2, 18));
  }

  [Test]
  public void GSingle_RolledBackPredicateWriteDoesNotAbortTheOtherWriter()
  {
    // FoundationDB's g-single-write-2 interleaves the delete before the
    // second update, then rolls it back instead of committing a cycle.
    using var t1 = Begin();
    using var t2 = Begin();
    Assert.That(Read(t1, 1), Is.EqualTo(10));
    Assert.That(Rows(t2), Is.EqualTo(new[] { (1, 10), (2, 20) }));
    t2.Upsert(1, 12);
    Assert.That(DeleteWhere(t1, value => value == 20), Is.EqualTo(1));
    Assert.That(t1.ContainsKey(2), Is.False);
    t2.Upsert(2, 18);
    t1.Rollback();
    Assert.That(t1.State, Is.EqualTo(TransactionState.Aborted));

    Commit(t2, true);
    AssertCommitted((1, 12), (2, 18));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void G2Item_WriteSkewIsAllowedOnlyBySnapshot(bool secondCommitsFirst)
  {
    using var t1 = Begin();
    using var t2 = Begin();
    // Point reads exercise item dependencies without also tracking a range.
    Assert.That(Read(t1, 1), Is.EqualTo(10));
    Assert.That(Read(t1, 2), Is.EqualTo(20));
    Assert.That(Read(t2, 1), Is.EqualTo(10));
    Assert.That(Read(t2, 2), Is.EqualTo(20));
    t1.Upsert(1, 11);
    t2.Upsert(2, 21);

    var bothCommit = IsolationLevel == TransactionIsolationLevel.Snapshot;
    Commit(secondCommitsFirst ? t2 : t1, true);
    Commit(secondCommitsFirst ? t1 : t2, bothCommit);
    AssertCommitted((1, bothCommit || !secondCommitsFirst ? 11 : 10),
        (2, bothCommit || secondCommitsFirst ? 21 : 20));
  }

  [TestCase(false, false)]
  [TestCase(false, true)]
  [TestCase(true, false)]
  [TestCase(true, true)]
  public void G2_PredicateWriteSkewIsAllowedOnlyBySnapshot(
      bool differentPredicates, bool secondCommitsFirst)
  {
    using var t1 = Begin();
    using var t2 = Begin();
    Assert.That(Rows(t1, value => value % 3 == 0), Is.Empty);
    Assert.That(Rows(t2, value => value % (differentPredicates ? 5 : 3) == 0),
        Is.EqualTo(differentPredicates ? new[] { (1, 10), (2, 20) } : Array.Empty<(int, int)>()));

    // Oracle's general G2 case uses different predicates and 60 rather than 42.
    var secondValue = differentPredicates ? 60 : 42;
    t1.Upsert(3, 30);
    t2.Upsert(4, secondValue);
    var bothCommit = IsolationLevel == TransactionIsolationLevel.Snapshot;
    Commit(secondCommitsFirst ? t2 : t1, true);
    Commit(secondCommitsFirst ? t1 : t2, bothCommit);

    if (bothCommit)
      AssertCommitted((1, 10), (2, 20), (3, 30), (4, secondValue));
    else if (secondCommitsFirst)
      AssertCommitted((1, 10), (2, 20), (4, secondValue));
    else
      AssertCommitted((1, 10), (2, 20), (3, 30));
  }

  [Test]
  public void G2_TwoAntiDependencyEdgesAreAllowedOnlyBySnapshot()
  {
    // Fekete's cycle: T1 reads old key 2; T2 updates it; T3 sees T2 and
    // old key 1; T1 then changes key 1. T3 is the read-only participant.
    using var t1 = Begin();
    Assert.That(Rows(t1), Is.EqualTo(new[] { (1, 10), (2, 20) }));
    using (var t2 = Begin())
    {
      t2.Upsert(2, Read(t2, 2) + 5);
      Commit(t2, true);
    }
    using (var t3 = Begin())
    {
      Assert.That(Rows(t3), Is.EqualTo(new[] { (1, 10), (2, 25) }));
      Commit(t3, true);
    }

    t1.Upsert(1, 0);
    var firstCommits = IsolationLevel == TransactionIsolationLevel.Snapshot;
    Commit(t1, firstCommits);
    AssertCommitted((1, firstCommits ? 0 : 10), (2, 25));
  }
}
