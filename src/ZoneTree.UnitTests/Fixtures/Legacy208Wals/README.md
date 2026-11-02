These fixtures were generated with the unmodified `release-v2.0.8` tag,
commit `4bc1e09`, using .NET 10 on Windows. They cover the plain WAL and both
compressed modes with None, LZ4 and Zstd compression.

Each database contains integer keys 0 through 149 with values `key + 1`.
The mutable segment was rotated before key 75. The WAL block size was 2048
bytes (using the release's unsafe-options setting only to keep fixtures small),
so both segment WALs contain a full block and a partial legacy tail. Explicit
WAL synchronization and metadata saving completed before disposal.

The migration tests open these files with 2.1.0, verify all original records,
append and sync another record, verify the version-1 tail upgrade, and reopen.
