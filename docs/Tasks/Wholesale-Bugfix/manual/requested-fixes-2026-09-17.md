# Requested bug fixes — 2026-09-17

All twelve requested issues have separate fix commits, pushed to
`codex/manual-wholesale-bugfix` for PR #2908. Before each commit, four fresh
`gpt-5.6-sol` reviewers ran without conversation forks. Findings above nits were
fixed and followed by another four-reviewer round; every final round approved.

| Issue | Fix commit | Review rounds |
| --- | --- | --- |
| #2898 | `85ffbe81d` | 1 |
| #2891 | `6e694d8a3` | 2 |
| #2909 | `c6b482c83` | 1 |
| #2896 | `993270331` | 3 |
| #2893 | `d802f2163` | 3 |
| #2895 | `ebba59502` | 2 |
| #2894 | `da72614fb` | 2 |
| #2847 | `6d5a8c871` | 1 |
| #2892 | `7bf8bd575` | 1 |
| #2902 | `9125cd197` | 2 |
| #2906 | `e36577228` | 1 |
| #2904 | `d1f4b89aa` | 1 |

A separately reviewed test-only follow-up aligns older assertions with the new
diagnostic and canonical document-ordering contracts, isolates stale shared
`demo.db` fixtures, and initializes the process-wide default collation before
temporarily switching test culture. Four fresh reviewers approved its final
state. Its 66 focused tests pass.

## Validation

| Run | Passed | Failed | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
| Starting revision `55b42d549`, .NET 8 | 2141 | 208 | 8 | 2357 |
| Final .NET 8 | 2224 | 176 | 8 | 2408 |
| Final .NET 10 | 2224 | 176 | 8 | 2408 |

The two final frameworks have identical failing test sets. All 176 failures
also fail on the starting revision: 32 previously failing tests now pass, 51
test cases were added, and no new failing tests remain. Of the remaining
failures, 166 are in `Audit2026`; the other ten cover #2093 (two), #2777 (one),
#2799 (one), #2823 (two), and #2855 (four). These are outside the requested list.
Exact test names and counters are in `requested-fixes-2026-09-17-tests.json`.

Commands used:

```sh
dotnet build LiteDB.sln -c Release -p:TestingEnabled=true
dotnet test LiteDB.Tests/LiteDB.Tests.csproj -c Release -f net8.0 -p:TestingEnabled=true --settings tests.runsettings
dotnet test LiteDB.Tests/LiteDB.Tests.csproj -c Release -f net10.0 -p:TestingEnabled=true --settings tests.runsettings
python3 scripts/test-vector-compatibility.py
```

Release solution compilation has zero errors, including the .NET Framework test
targets. The vector compatibility check passes against LiteDB 5.0.21 for plain
and encrypted ordinary/vector files. The `win-x86` test project cross-build,
workflow YAML parsing, and runtime-installer PowerShell syntax checks pass.
The host-architecture assertion passes for x64 and deliberately fails when x86
is requested on the local x64 host. Actual Windows x86 execution is not yet
verified: GitHub CI remains queued awaiting runners.

## Compatibility and remaining limitations

- Document and mixed numeric ordering changes revise the comparer stamp.
  Older nonzero-stamped files require export with the original engine and
  import with the new engine; see `docs/collation-runtime-compatibility.md`.
- The new repository array/list interface overloads are a documented v6
  interface compatibility change; see `docs/repository-bulk-overloads.md`.
- Mapper failures retain original exceptions inside contextual diagnostics;
  non-document roots now fail explicitly. See `docs/mapper-error-diagnostics.md`.
- Legacy literal-based `Query.EQ` can still lose numeric precision through
  expression formatting/caching. This is separate from #2902's comparer fix;
  use the existing opt-in `Query.Parameterized` API or typed parameters.
