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

## Attached audit evidence

- [Narrative audit](https://github.com/JKamsker/LiteDB/blob/codex/audit-2026-regression-tests/docs/audits/2026-09/code-audit-2026-09.md)
- [Complete findings ledger](https://github.com/JKamsker/LiteDB/blob/codex/audit-2026-regression-tests/docs/audits/2026-09/findings-full.json)
- [Independent SQL LIKE reference port](https://github.com/JKamsker/LiteDB/blob/codex/audit-2026-regression-tests/docs/audits/2026-09/sqllike_port.py)

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
