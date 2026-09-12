# Memory profiles

Profiles provide explicit defaults for a database's cache and transaction budget.
They do not detect host RAM, reserve their entire cache on open, or change while
the database is running. `Balanced` preserves the existing defaults.

| Profile | File / temp cache target | Memory-backed cache target | Transaction page threshold | Starting point for |
| --- | ---: | ---: | ---: | --- |
| `LowMemory` | 8 MiB | 4 MiB | 256 | Small hosts, many open databases, mostly small transactions |
| `Balanced` (default) | 64 MiB | 8 MiB | 1,000 | General-purpose applications |
| `Throughput` | 128 MiB | 16 MiB | 4,000 | Larger reused working sets, bulk writes and index builds |

The cache grows on demand. Targets are rounded to whole segments and are soft:
active operations can temporarily exceed them when frames are pinned, writable,
or loading. Transaction thresholds are cooperative safepoints, not hard byte
limits. With 8 KiB pages, 256 / 1,000 / 4,000 pages represent approximately
2 / 7.8 / 31.3 MiB of page data **per active transaction**, before other engine
and application allocations. Consider concurrent transactions and the number of
database engines when choosing a profile.

Memory-backed storage includes `:memory:` and a supplied `MemoryStream`. The
actual data in a memory-backed database is separate from its cache and remains
in memory regardless of the profile. A cache target is not an RSS or total-memory
cap. All profiles use the same ownership checks and durability behavior, and
the compiled-expression cache remains bounded to 1,000 entries.

## Configuration

```csharp
using var database = new LiteDatabase(
    "filename=app.db;memory profile=LowMemory");
```

The same property is available on `ConnectionString` and `EngineSettings`:

```csharp
using var database = new LiteDatabase(new ConnectionString("app.db")
{
    MemoryProfile = MemoryProfile.Throughput,
    CacheSize = 64L * 1024 * 1024
});
```

This combines a 64 MiB cache target with the Throughput profile's 4,000-page
transaction threshold. It can help write-heavy workloads without increasing
the retained cache target, but active transactions can still retain more pages.
Measure the combined memory demand before selecting it for a small host.

Explicit `CacheSize` and `TransactionPageLimit` values always win, regardless of
property assignment or connection-string key order. An unspecified transaction
limit follows the selected profile. `CacheSize = 0` selects the profile's
storage-specific default; it does not request an unbounded cache. Explicit
transaction limits must be greater than zero.

```text
filename=app.db;memory profile=Throughput;cache size=64MB;transaction pages=2000
```

Profile names are case-insensitive in connection strings. Invalid or numeric
names are rejected. Configure the profile and overrides before opening the
engine. `$database.cache.memoryProfile` reports the selected profile;
`$database.cache.limitBytes` and `$database.transactions.transactionPageLimit`
report the effective limits, including overrides.

## Choosing and measuring

Start with Balanced, or LowMemory when the application has a small memory
allowance or keeps many databases open. For repeated reads, size the cache for
the frequently reused pages rather than the entire file. A one-pass scan may
gain little from a larger cache. For large write transactions, compare larger
transaction thresholds while holding the cache size constant: fewer safepoints
can reduce repeated WAL writes, but increase transient retention.

Profile names are starting points, not performance guarantees. The
[profile measurements](memory-profile-results.md) compare the implementation
against the preceding merged PR revision. The
[runner](../tools/MemoryProfiles/README.md) records latency distributions,
throughput, monitor contentions, retained managed memory, and cache accounting.
