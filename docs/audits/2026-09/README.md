# September 2026 audit regression coverage

## Problem

The September 2026 audit identified 170 canonical findings in the LiteDB
snapshot at commit `a50661a`. This test-only pull request records those findings
as executable regression targets before production fixes are split into focused
changes.

The audit also contains three refuted reports. They are retained in the evidence
but deliberately excluded from the regression ledger.

## Approach

- Add 60 safe, deterministic regression cases that assert the intended public
  contract across expressions, JSON, queries, the engine, SQL, and mapping.
- Add one bounded source-context guard for each of the 170 canonical findings.
  The guards cover the complete ledger, including findings whose direct trigger
  would require power loss, deliberate corruption, unbounded allocation or
  recursion, process termination, host-account changes, a cross-process hang, or
  a narrowly timed race.
- Add a passing coverage-integrity test that compares the guard IDs with the
  canonical `unique` array in the attached findings file.

The regression and source-guard suites intentionally fail on the audited
baseline. This makes the unfixed behavior visible. A source guard is transitional:
when production code fixes a finding, replace the guard with a deterministic
behavior or fault-injection test. A changed source context does not, by itself,
prove that the final behavior is correct.

## Prior upstream coverage

The upstream search covered 2,810 issue and pull-request records in all states.
The classification below is conservative: "tracked" means the same mechanism or
behavior is described directly, while "related" means an existing umbrella issue
or implementation PR covers the subsystem but not the exact audit case. Closed
items are not assumed fixed; some were closed as duplicates or have a residual
case in the audited code.

### Already tracked directly (34 findings)

| Audit finding IDs | Existing issue or pull request |
| --- | --- |
| 1 | [#2818](https://github.com/litedb-org/LiteDB/issues/2818) — WAL commits are not durably flushed |
| 2, 66 | [#2824](https://github.com/litedb-org/LiteDB/issues/2824) — encrypted/default/SQL rebuild failures |
| 5, 65 | [#2787](https://github.com/litedb-org/LiteDB/issues/2787), [#2790](https://github.com/litedb-org/LiteDB/issues/2790) — shared mutex recursion and thread affinity |
| 7, 12–15 | [#2797](https://github.com/litedb-org/LiteDB/issues/2797) — LIKE hangs, wildcard semantics, and unescaped LINQ input |
| 17 | [#2800](https://github.com/litedb-org/LiteDB/issues/2800) — stale compiled nested-expression parameters |
| 28 | [#1943](https://github.com/litedb-org/LiteDB/issues/1943) — `EnsureIndex` does not upgrade uniqueness |
| 38, 149 | [#2819](https://github.com/litedb-org/LiteDB/issues/2819), duplicate [#2768](https://github.com/litedb-org/LiteDB/issues/2768) — extended sort keys |
| 43 | [#2848](https://github.com/litedb-org/LiteDB/issues/2848) — transaction release after commit/rollback failure |
| 44 | [#2790](https://github.com/litedb-org/LiteDB/issues/2790) — query transaction released on another thread |
| 46 | [#2806](https://github.com/litedb-org/LiteDB/issues/2806) — non-atomic FileStorage overwrite |
| 70, 92 | [#2777](https://github.com/litedb-org/LiteDB/issues/2777) — AES-ECB, missing integrity, and weak KDF |
| 75, 152–155 | [#2826](https://github.com/litedb-org/LiteDB/issues/2826) — malformed/truncated legacy page chains |
| 86 | [#2859](https://github.com/litedb-org/LiteDB/issues/2859) — nested document/array collation |
| 110 | [#2801](https://github.com/litedb-org/LiteDB/issues/2801) — malformed `BsonValue(object)` document/array values |
| 111 | [#1224](https://github.com/litedb-org/LiteDB/issues/1224), [#2751](https://github.com/litedb-org/LiteDB/pull/2751) — UInt64 `BsonValue` conversion |
| 128 | [#2845](https://github.com/litedb-org/LiteDB/issues/2845) — JSON double precision loss |
| 132 | [#2871](https://github.com/litedb-org/LiteDB/issues/2871) — constructor-cache race |
| 143 | [#2858](https://github.com/litedb-org/LiteDB/issues/2858) — culture-sensitive SQL keyword parsing |
| 159 | [#2767](https://github.com/litedb-org/LiteDB/issues/2767), [#2523](https://github.com/litedb-org/LiteDB/issues/2523), [#2717](https://github.com/litedb-org/LiteDB/pull/2717) — legal short reads |
| 168 | [#2764](https://github.com/litedb-org/LiteDB/issues/2764) — incorrect `AesStream.Seek` offsets |
| 177 | [#2815](https://github.com/litedb-org/LiteDB/issues/2815) — read-only disposal opens/deletes the log |
| 199 | [#2843](https://github.com/litedb-org/LiteDB/issues/2843) — shell loop at unterminated EOF |

The issue-regression work in [#2885](https://github.com/litedb-org/LiteDB/pull/2885),
which supersedes closed draft #2877, contains regression commits for many of
these issue threads. The issue inventory and this audit ledger are consolidated
in that pull request but remain separate because they use different identifiers
and validation semantics. The audit is complementary, not a claim that all 34
reports are new.

### Existing umbrella or post-fix follow-up (21 findings)

| Audit finding IDs | Related work and distinction |
| --- | --- |
| 26, 27, 31, 99, 102, 103, 195 | [#2881](https://github.com/litedb-org/LiteDB/issues/2881) and merged [#2882](https://github.com/litedb-org/LiteDB/pull/2882); these are residual vector cases found against the post-merge code |
| 37 | [#2767](https://github.com/litedb-org/LiteDB/issues/2767) and [#2717](https://github.com/litedb-org/LiteDB/pull/2717) cover short reads, but not stale pooled-page publication |
| 48 | [#2869](https://github.com/litedb-org/LiteDB/issues/2869) and [#2833](https://github.com/litedb-org/LiteDB/issues/2833) cover numeric type fidelity, but not the mapper setter path |
| 52 | [#2770](https://github.com/litedb-org/LiteDB/issues/2770), [#2322](https://github.com/litedb-org/LiteDB/issues/2322), and [#2885](https://github.com/litedb-org/LiteDB/pull/2885) cover enum-expression failures, but not unnamed flags combinations |
| 62, 64, 139, 140 | [#2787](https://github.com/litedb-org/LiteDB/issues/2787), [#2790](https://github.com/litedb-org/LiteDB/issues/2790), and [#2848](https://github.com/litedb-org/LiteDB/issues/2848) cover shared/transaction cleanup; these are distinct exception paths |
| 69 | [#2777](https://github.com/litedb-org/LiteDB/issues/2777) describes the known password-verification block and missing integrity; the all-zero bypass is a new concrete case |
| 73 | [#2797](https://github.com/litedb-org/LiteDB/issues/2797) and [#2506](https://github.com/litedb-org/LiteDB/issues/2506) cover LIKE and the parameterized FileStorage query, but not null/non-string indexed parameters |
| 134 | [#1675](https://github.com/litedb-org/LiteDB/issues/1675) and [#928](https://github.com/litedb-org/LiteDB/pull/928) cover dictionary key typing, but not non-generic subclasses |
| 136 | [#2255](https://github.com/litedb-org/LiteDB/issues/2255), merged [#2753](https://github.com/litedb-org/LiteDB/pull/2753), and [#2883](https://github.com/litedb-org/LiteDB/pull/2883) cover invariant dictionary keys; this is the remaining DateTimeOffset edge |
| 141 | [#2793](https://github.com/litedb-org/LiteDB/issues/2793) covers shared-mutex platform/name failures, but not long Unix paths bypassing the hash fallback |
| 157 | [#2815](https://github.com/litedb-org/LiteDB/issues/2815) covers destructive read-only maintenance, but not the recovery rename/replace path |
| 173 | [#2828](https://github.com/litedb-org/LiteDB/issues/2828) covers a fatal `TransactionService` finalizer; this finding is the separate `PageBuffer` finalizer |

### No direct prior match found (115 new findings)

The following audit IDs had no direct issue or pull-request match in titles and
bodies. They are treated as new findings pending maintainer review:

`4, 8, 9, 11, 16, 18, 19, 20, 21, 22, 23, 24, 25, 29, 33, 36,
40, 41, 42, 45, 49, 50, 51, 53, 54, 55, 56, 58, 59, 60, 61, 67,
68, 72, 76, 77, 78, 80, 82, 83, 85, 87, 88, 89, 90, 93, 95, 96,
97, 98, 100, 101, 104, 105, 106, 107, 108, 109, 112, 113, 114,
115, 116, 117, 118, 119, 120, 123, 125, 127, 129, 130, 131, 133,
135, 137, 138, 142, 144, 145, 146, 147, 148, 151, 156, 158, 160,
167, 169, 170, 171, 172, 174, 175, 176, 178, 179, 180, 181, 182,
183, 184, 185, 186, 187, 188, 189, 191, 192, 193, 194, 196, 198,
202, 203`.

This search covered issue and PR titles and bodies. A mention that exists only in
an old comment may not appear in this classification.

## Attached audit evidence

- [Narrative audit](code-audit-2026-09.md)
- [Complete findings ledger](findings-full.json)
- [Independent SQL LIKE reference port](sqllike_port.py)

The committed copies are byte-for-byte identical to the supplied attachments.

## Validation

- Baseline suite before adding the expected-failing regressions: 864 passed,
  7 skipped, 0 failed (`net8.0`).
- Release test-project build: succeeds.
- The whole-solution build is unavailable in this Linux environment because the
  `net462` and `net481` reference assemblies are not installed; the affected
  `net8.0` library and test projects build successfully.
- `AuditBehavior`: 60 failed as expected, 0 passed.
- `AuditSourceGuard`: 170 failed as expected, 0 passed.
- `AuditInfrastructure`: expected to pass and proves all canonical finding IDs
  are represented.

Because this is a test-first pull request, the complete suite remains red until
the findings are fixed or the tests are adopted alongside their corresponding
production fixes.
