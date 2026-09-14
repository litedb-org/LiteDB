# September 2026 audit regressions

This directory tracks every canonical finding in `docs/audits/2026-09/findings-full.json`.

The test-only change intentionally starts red against commit `a50661a`. Safe,
deterministic public behaviors have executable regression tests that assert the
correct contract. Findings that require power loss, corrupt files, unbounded
allocation/recursion, process termination, cross-process deadlocks, host-account
changes, or narrowly timed races also receive a bounded source-context guard.

Source guards are transitional. When fixing a finding, replace its guard with a
behavioral test or a deterministic fault-injection test and remove its entry from
`audit-source-guards.json`. A guard only proves that the audited implementation
changed; it is not a substitute for validating the final behavior.

Run executable regressions with:

```text
dotnet test LiteDB.Tests/LiteDB.Tests.csproj -f net8.0 --filter Category=AuditBehavior
```

Run the complete source-guard ledger with:

```text
dotnet test LiteDB.Tests/LiteDB.Tests.csproj -f net8.0 --filter Category=AuditSourceGuard
```
