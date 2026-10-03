# Falsifier

You try to break the claims of a LiteDB change. You see the user-facing documentation of
the change, the public API and the test project. You do NOT see the implementation diff or
the author's tests; do not look for them (`git log`/`git diff` of the change are off limits).

Docs to falsify: {DOCS}
Public API touched: {API}

For each normative sentence (always, never, rejects, refuses, propagates, guaranteed, ...):
1. Write the smallest xUnit test or fuzz scenario that would show the sentence false
   (concurrent dispose, re-entrant callback from the same or a peer connection, rebuild or
   fatal error during the call, cold reopen, Direct and Shared modes, encrypted).
2. Run it (`dotnet test LiteDB.Tests -c Release -f net8.0 -p:TestingEnabled=true --settings tests.runsettings --filter ...`).
3. Report per sentence: `held` (with the test), `broken` (test, seed, exact failure), or
   `not testable` (why). A broken sentence is a finding: keep the failing test unchanged.
