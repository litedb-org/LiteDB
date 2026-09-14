# Issue 2804: runtime expression compilation under full AOT

This repro covers [issue #2804](https://github.com/litedb-org/LiteDB/issues/2804), where LiteDB
compiles expression trees at runtime and Mono full-AOT cannot create the resulting dynamic methods.
It tests package `5.0.21` and the current source tree independently.

The host probe has two parts:

1. It publishes and executes a Linux NativeAOT binary. The binary requires
   `RuntimeFeature.IsDynamicCodeSupported` and `IsDynamicCodeCompiled` to both be `false`, then
   checks an interpreted expression-tree control, a reflection control, a LiteDB BSON expression,
   and typed mapper getters/setters. This currently passes and demonstrates that .NET NativeAOT is
   not interchangeable with the failing Mono runtime.
2. In the pinned Mono 6.12 container, it compiles one program and first runs it under the ordinary
   JIT. It then full-AOT-compiles the program, LiteDB, and every managed dependency before rerunning
   the same program with `mono --full-aot`. Separate sacrificial processes prove that a freshly
   compiled custom expression delegate succeeds under JIT and is rejected under full AOT with
   Mono's exact no-JIT signature; this prevents a JIT run mislabeled by a command-line argument from
   satisfying the fixed-build path. A static-delegate/reflection control then runs before the first
   LiteDB access in both modes. The LiteDB path checks BSON expression evaluation, typed mapping
   (including a struct and array), CRUD, a captured LINQ predicate, a string BSON predicate, and
   indexed and unindexed results.

This is a Linux Mono full-AOT analogue, not an iOS, MAUI, Unity IL2CPP, or device test. It reproduces
the same runtime constraint and the same LiteDB expression-compilation signature reported by the
issue, but it does not claim that a particular Apple or Unity toolchain was exercised. In particular,
the Linux NativeAOT control already passes on the unfixed code because that runtime can interpret
the delegate shapes used here. It is a trimming/runtime control, not evidence that `net8.0-ios` is
fixed. A future repair still needs a real iOS full-AOT run or an instrumented, forced-fallback test
for LiteDB's `net8.0` target before #2804 can be closed for modern .NET iOS.

## Prerequisites and run

The machine needs a .NET SDK with the Linux NativeAOT toolchain and Docker. Docker downloads the
pinned multi-architecture image by its exact digest when it is not already cached. It can also be
preloaded explicitly:

```bash
docker pull mono:6.12.0.182@sha256:34d816779b1248b5cfd095770b64ecbaf1798e2aca693a91c11a018dce9c7ad5
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- \
  run Issue_2804_AotRuntime
```

NativeAOT publish failure, image download or container-start failure, in-container C# compilation
failure, Mono AOT-compilation failure, a timeout, and an unexpected runtime exception are harness
errors (exit `20` from the repro) rather than confirmations. Phase markers and captured
publisher/compiler diagnostics identify failures before the target runtime call.

Exit `0` plus `BUG_2804_CONFIRMED` requires all relevant controls to pass followed by a LiteDB frame
and either the exact Mono `ExecutionEngineException: Attempting to JIT compile method` signature or
an explicit unsupported-dynamic-code exception. Before that target process starts, a distinct
full-AOT process must prove that runtime compilation is actually forbidden; its expected exception
is deliberately not copied into the target output. On the affected builds, the first static
`BsonExpression` access reaches the verdict before mapper/query checks can run. Exit `10` plus
`VERIFIED_2804` is reserved for a build where the Linux NativeAOT probe and both the Mono JIT and
full-AOT LiteDB oracles all pass. That verifies the Linux analogue only, not the Apple/Unity matrix
above. Keep the exact exit and marker expectations when changing the manifest to green after a fix;
accepting an arbitrary nonzero exit could mislabel a build or runtime failure as a repair.

## Last verified

The fresh-CI-cache failure was corrected by explicitly downloading the three
pinned Mono dependency packages. A `net8.0` host does not restore those older
`netstandard2.0` assets transitively. Staging also accepts `NUGET_PACKAGES`
without a trailing separator. Both variants were rerun successfully with a new,
empty package directory; all no-JIT controls and exact bug signatures passed.

Rechecked on 2026-09-14 after merging dev `a50661a9`: both 5.0.21 and the
source variant again passed the NativeAOT/JIT/no-JIT controls and emitted the
exact `BUG_2804_CONFIRMED` marker. ReproRunner's green comparison means the
expected bug was reproduced in both variants; it does not mean the bug is fixed.

Validated on Linux x64 with .NET SDK 10.0.400 targeting `net8.0`, .NET runtime 8.0.30, and the pinned
amd64 Mono image. Package 5.0.21 and the current source variant both passed the NativeAOT probe,
Mono JIT probe, and full-AOT compilation. Both printed
`MONO_JIT_PRECONDITION_PASSED_2804`, followed later by
`MONO_NO_JIT_PRECONDITION_PASSED_2804` and
`MONO_RUNTIME_CONTROL_PASSED_2804: mode=full-aot` before failing from
`LiteDB.BsonExpression.Compile` with:

```text
System.ExecutionEngineException: Attempting to JIT compile method '(wrapper dynamic-method) ...'
while running in aot-only mode
```

Manifest validation and the two-variant run both passed their declared expectations.
