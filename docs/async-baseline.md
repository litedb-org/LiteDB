# Async-first Baseline

This document captures the shared assumptions behind the LiteDB async-first overhaul. It lists the primitives every new API will
use and the build-time dependencies that keep the multi-targeted package consistent.

## Core async primitives

LiteDB code and new public entry points will prefer the following types:

- `Task` for asynchronous operations that do not need custom pooling.
- `ValueTask` for performance sensitive paths where allocations must stay predictable.
- `IAsyncEnumerable<T>` for streaming results from queries, cursors, and file storage.
- `CancellationToken` for all operations that can be interrupted by the caller.
- `IAsyncDisposable` for database, engine, and reader lifetimes.

These primitives are supported on all target frameworks the library currently ships (netstandard2.0 and net8.0).

## Multi-target compatibility

`netstandard2.0` does not ship the newer async abstractions in-box, so the project references
`Microsoft.Bcl.AsyncInterfaces` to provide them. Consumers on .NET Framework or Xamarin will therefore receive the necessary
interfaces transitively, while `net8.0` builds fall back to the implementations already available in the runtime.

## Guidance for dependent projects

- Keep the package reference to `Microsoft.Bcl.AsyncInterfaces` for the `netstandard2.0` target in `LiteDB.csproj`.
- Do not add `Microsoft.Bcl.AsyncInterfaces` to the `net8.0` target. The types exist in the base class library and pulling the
  package would only increase deployment size.
- When new projects are added to the solution, ensure they either target a framework where the async interfaces are in-box or
  reference the same package explicitly.

Following this baseline lets the rest of the async-first work proceed without forcing breaking framework changes.
