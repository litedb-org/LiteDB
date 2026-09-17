# Expression execution on AOT-only runtimes

LiteDB checks the runtime dynamic-code capability flag when available, then probes
compilation and invocation of its actual custom delegate signatures. Runtimes that
cannot execute generated methods use statically compiled delegate adapters and a
reflection interpreter for BSON expressions, mapper constructors/accessors and
captured LINQ values. Normal JIT runtimes retain compiled expression delegates.
Mono 6.12's `preferInterpretation` option does not provide this fallback.

The fallback supports the expression shapes emitted by LiteDB's BSON parser and
mapper, including arrays, nested expressions, conditional and short-circuit
execution. Captured CLR expressions outside the interpreter's supported shapes
throw `NotSupportedException`; precompute those values before constructing the
query. In particular, nested CLR selector/predicate lambdas in captured calls such
as `values.First(v => v.Id > 0)`, `values.Where(...)` or `values.Max(v => v.Date)`
require precomputation. For example, calculate `var cutoff = values.First(v =>
v.Id > 0).Id;` before passing `row => row.Id == cutoff` to LiteDB. The ordinary
LINQ call then uses the application's statically compiled delegate. Those inline
forms remain supported by the normal JIT path. It is not a general replacement for the CLR expression compiler. Preserve
reflection metadata for mapped types when trimming an application.

Validation covers the pinned Mono full-AOT process, Linux NativeAOT, and forced
fallback tests on the net8.0 library including CRUD, typed structures/arrays,
indexes and nested SQL aliases. The Linux harness does not exercise an Apple or
Unity device/toolchain; the forced net8.0 test verifies LiteDB's fallback path,
not every host's trimming configuration.

For older hosts without a reliable dynamic-code flag, especially Unity WebGL,
call `AppContext.SetSwitch("LiteDB.UseInterpreter", true)` before any LiteDB use.
This bypasses all dynamic compilation probes, including native failures that a
managed exception handler cannot catch. Set it once during process startup.
