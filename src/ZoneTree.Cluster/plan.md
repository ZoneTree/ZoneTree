# ZoneTree.Cluster plan

The goal is the fastest possible in-process cluster first. The cluster is released only with
full ACID guarantees. Remote access and distribution come after it. Open technical work that
is not a phase lives in [follow-up.md](follow-up.md).

| Phase | Content | Status |
| --- | --- | --- |
| 1. Cluster catalog | The catalog as the single source of truth, stable ids, the encoding registry, the schema API | Done |
| 2. Schema lifecycle | Drop and rename for columns and domains, recovery of a drop a crash interrupted | Done |
| 3. ACID across columns | One clock per domain, commits in timestamp order, atomic visibility and recovery, serializable validation across columns | Done |
| 4. Backup and restore | A consistent backup at a domain snapshot, and restore | Postponed |
| 5. Remote access | The client/host protocol | Next |
| 6. Distribution | Replication, sharding, multiple coordinators per domain | Later |

## Phase 4: backup and restore (postponed)

- A backup is one consistent cut at a domain snapshot timestamp: the catalog, every column
  (through the engine's live backup), and the transaction records.
- Restore rebuilds the cluster and applies crash recovery to that cut.
- This replaces the `NotSupportedException` in `CreateLiveBackup` and `RestoreInProcessAsync`.
- Tests: a backup under load, a restore, and a crash during a backup.

## Phase 5: remote access (next)

- **The client/host protocol and `ConnectAsync`:**
  - client sessions, and transaction leases that the client renews;
  - schema epochs checked on every request, with the client refreshing its cached catalog
    when it is stale;
  - write responses that carry the `HasConflict` flag;
  - the snapshot timestamp of a transaction, as in-process.
- **Remote clients keep the in-process features:** the transaction options (isolation,
  timeout), cache and prefetch settings, and outcome lookups.
- **The domain coordinator is the remote boundary.** The clock is behind
  `ITimestampOracle`.
- **Decision needed before this phase:** whether remote storage nodes store the user's types,
  which the host registers, or bytes in an order-preserving encoding.

## Phase 6: distribution (later)

- The catalog and each domain's timestamp oracle are replicated through a consensus log
  (Raft). The catalog's commands are already a log.
- A placement map: column shards by key range, their replicas, and the nodes that own them.
- Any number of coordinators per domain, holding no state.
- The transaction record moves to the shard of the transaction's primary key (Percolator).

## Release blockers

- **Kill-process crash tests,** as the final step. See [acid.md](acid.md) and
  [follow-up.md](follow-up.md).

## How we work

- **Before each phase:** a plan check-in.
- **During a phase:**
  - focused tests only, plus a build of every target framework;
  - benchmark runs of at most 3 minutes;
  - crash tests only as the final step, and only when asked.
- **Engine changes:** only when a phase or a measured gain needs them, and after agreeing on
  them.
- **Formats:** the cluster is unreleased, so its formats change in place with no migration.
