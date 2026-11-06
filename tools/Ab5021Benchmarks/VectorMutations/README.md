The **Vector mutation performance (report only)** workflow builds isolated Release
assemblies with `TestingEnabled=false` on Linux/Windows and net8/net10. PR runs use
10,000 vectors; dispatch with `count=100000` for the larger workload and optionally
`baseline_ref=6ec0b80dbf9da55846ad71727b1f76c764ac5eed` to compare the previous repair.
Both variants use identical candidate harness sources. This is a v6 comparison;
5.0.21 has no native vector index API.

JSON artifacts identify commits, assembly hashes, runtime, dimensions and seed,
and report build/insert/update/delete time, allocated bytes, file sizes, exact
survivor identity/count checks and whether the explicit deferred-repair signal
occurred after each phase. Once deferred, later operations maintain documents only;
those samples do not measure active graph maintenance. Setup/build and mutation phases are separate; the existing graph lookup
can still dominate large-N setup. No performance threshold gates the results.
A harness/oracle error fails the measurement instead of fabricating a sample.
The runner refuses execution outside GitHub Actions. Do not run it locally.
