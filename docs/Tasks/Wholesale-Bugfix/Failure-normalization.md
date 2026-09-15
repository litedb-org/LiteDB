# Reviewed failure normalization

The full-matrix grader compares exact failure text after applying the policy in
`scripts/bugfix/failure-normalization.json`. The policy is scoped by full test
name. A baseline that does not match every rule fails closed; an unrecognized
candidate shape remains unchanged and therefore cannot compare equal.

## Issue 2870 first-source stack

The reviewed test is
`LiteDB.Tests.Issues.Issue2870_Tests.Query_preserves_the_original_frame_when_the_first_source_read_throws`.
Its raw assertion still fails because the stack does not contain
`ThrowAtOriginalSourceSite`. Normalization does not make the test pass or remove
that assertion.

Frozen source `dd937719f7eee53c512f50ac604cab639bf42a4c` has two iterator wrappers
around `RunQuery`. Runtime inlining and sequence-point selection produced these
five exact adjacent renderings:

1. One unlocated `OnDispose` frame, then `BsonDataReader` line 56.
2. One `OnDispose` line 13 frame, then `BsonDataReader` line 46.
3. Two `OnDispose` line 13 frames, then `BsonDataReader` line 56.
4. One unlocated and one line 13 `OnDispose` frame, then `BsonDataReader` line 56.
5. One line 13 and one unlocated `OnDispose` frame, then `BsonDataReader` line 56.

The rule admits only these sequences between the unchanged generated
`RunQuery` frame at `QueryExecutor.cs:line 139` and
`ExecuteQuery(Boolean executionPlan)` at line 87. Its canonical text retains
the generated `RunQuery` method, two named `OnDispose` methods, the
`BsonDataReader` constructor, the Boolean overload, and all following frames and
assertion text. A different method, count, order, source line, or neighbor is
not normalized.

The public `ExecuteQuery()` frame at line 59 is an inlining-dependent wrapper
around the retained Boolean overload. It is admitted only when directly between
that overload and `LiteEngine.Query(String collection, Query query)` at line 45.
The FluentAssertions `ActionAssertions.InvokeSubject()` wrapper is likewise
optional only immediately before the retained `DelegateAssertions` frame.
Exact source paths accept Windows or Unix separators; no general stack-frame
stripping occurs.

## Evidence

The source-identical build probe is GitHub Actions run `34981833553`. Its ARM64
net8 and net10 artifacts contain renderings also observed in independently built
baseline and candidate jobs. Reviewed paired artifact IDs include:

- ARM64 net8: baseline `10404446657`, candidate `10404105968`.
- ARM64 net9: baseline `10404905288`, candidate `10404151766`.
- ARM64 net10: baseline `10404626368`, candidate `10403778393`.
- macOS net9: baseline `10403923694`, candidate `10403912277`.
- macOS net10 baseline: `10404361598`.
- Windows 2022 x64 net10 baseline: `10403843631`.

Every record uses VSTest definition ID
`7d0d6fcd-0d48-7a6b-d09f-7c6134821dde`; per-run execution IDs correctly differ.
The acceptance evidence comes from GitHub Actions run `34988724926`.

Policy bytes use LF line endings. The reviewed policy SHA-256 is
`d4c510bf1504ec8298e0f231548fde8e67a929f7a3ccca9c6ec21a1a89571a36`.
