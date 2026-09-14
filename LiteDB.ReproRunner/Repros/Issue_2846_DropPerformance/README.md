# Issue 2846: large DropCollection performance

Source: https://github.com/litedb-org/LiteDB/issues/2846

The regression entry point is **`python3 scripts/regressions/issue_2846.py`**.
It runs the ReproRunner measurement fixture against NuGet 5.0.9 and dev, requires
all persistence controls to pass, and fails when dev's median exceeds twice the
5.0.9 median. The child manifest checks measurement validity only; its green
summary is not the performance verdict. Always run the comparison script.

Each variant takes three samples on 100,000 documents with 2 KB payloads and two
secondary indexes. Setup and verification are outside the timed region. After
each drop, reopen checks that no old rows remain, an unrelated collection is
intact, and recreating the dropped collection stores only the new generation.

Observed on dev `90788bace880f477c9fb52ff7c0da98c9700bdfe`, Linux / .NET 8:

- 5.0.9 median: 139.107 ms.
- Dev median: 376.514 ms.
- Ratio: 2.707; comparison **failed**.

Timing is host-sensitive. Run on a quiet machine. Set `LITEDB_REPRO_ROWS=1000000`
for the original report's scale; increase the child manifest timeout for three
multi-minute samples. The small run already reproduces a slowdown, but does not
claim the exact 101-second measurement from the report.
