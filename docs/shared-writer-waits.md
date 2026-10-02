# Shared writer waits

In Shared mode one connection at a time, in any process, owns a database's native
writer mutex. Writes, legacy transactions and explicit transaction handles need it,
and so do reads that a qualified mapped snapshot (.NET 8+) cannot serve. Other
callers wait ([#3080](https://github.com/litedb-org/LiteDB/issues/3080)).

## Refusing self-waits: `SharedSelfWaitGrace` (opt-in)

While a [transaction handle](transaction-handles.md) owns the mutex, an ordinary or legacy
call, or a second `BeginTransaction()`, from the async flow that began or used that handle
waits for the handle to complete. That is fine when another thread completes it, and a
permanent hang when that flow itself would have to. LiteDB cannot tell which, so by
default it waits (`SharedSelfWaitGrace` infinite).

```csharp
var db = new LiteDatabase("filename=app.db;connection=shared;shared self wait grace=00:00:02");
```

- `0`: refuse such a wait with `InvalidOperationException` before waiting.
- A positive grace: wait normally, and refuse once the flow has waited at least that long
  *and* the owning handle has been idle that long. A handle another thread is still using
  keeps its waiter waiting; a refused call has no side effect.

The flow follows `await` continuations and child tasks (an `AsyncLocal` marker holding a
weak reference); it only ever refuses, it never makes an ordinary call join the handle.
It also covers the same file through another connection. Calls from other flows always
wait as usual. Code that never ran in the handle's flow is not detected; use
`SharedWriterTimeout`. The check costs nothing while no Shared handle for the database is
open in the process.

## Bounding the wait: `SharedWriterTimeout`

```csharp
var db = new LiteDatabase("filename=app.db;connection=shared;shared writer timeout=30");
// or new EngineSettings { Filename = "app.db", SharedWriterTimeout = TimeSpan.FromSeconds(30) }
```

The default is infinite, which keeps the previous behavior. A finite timeout is one
budget for the whole wait: the connection's local queue, a pin's holder, the
cross-process turnstile and the native mutex, and for `BeginTransaction()` also the
local handle queue. When it runs out the call throws `LiteException` with error code
`LOCK_TIMEOUT` (120) before any side effect. The message names what the wait was behind:

- "another thread of this connection", when it queued behind one (that thread may itself
  wait for another owner);
- a transaction handle of this process, with how long it has held the mutex and been idle;
- "this process's transaction handle (admitting)", when a handle of this process holds
  the local handle queue but is not registered as the owner yet (it may itself still wait
  for another connection or process);
- otherwise "another connection or process".

The budget is spent only on waiting for another owner: this connection's own release of
its previous call, still completing on its holder thread, is waited for first and not
charged, so a zero budget ("try once") does not fail on an uncontended connection. A
timed-out wait leaves no turnstile, mutex or queue ownership behind. The turnstile
and mutex waits block with the remaining budget; they do not poll.

Values: whole seconds (`30`), a `TimeSpan` (`00:00:01.5`), or `infinite` / `-1`.
The timeout does not change the database `TIMEOUT` pragma, which still bounds
collection lock waits; reading that pragma itself needs the writer mutex.

## Diagnostics

```csharp
var d = db.GetSharedWaitDiagnostics();          // null unless Shared; last 5 minutes
// d.CurrentWaiters, d.LongestCurrentWait
// d.Owner (TransactionHandle | Unknown), d.OwnerHeld, d.OwnerIdle
// d.Recent / d.Total: Count, TotalWait, MaxWait, Over500Milliseconds, Over1Second, TimedOut, Refused
var lastHour = db.GetSharedWaitDiagnostics(TimeSpan.FromHours(1));
```

`SharedEngine.GetWaitDiagnostics(...)` returns the same snapshot for a caller-owned
engine. Statistics are per connection, kept in per-minute buckets for up to one hour.
`Count` includes acquisitions that did not have to wait. `Owner` is what this process
knows: a handle of this process, or `Unknown` (free, another connection, or another
process). A handle's begin records its own native acquisition on its connection.

To be notified of slow waits:

```csharp
settings.SharedSlowWaitThreshold = TimeSpan.FromMilliseconds(500);
settings.SharedSlowWait = info => logger.LogWarning(
    "Shared wait on {File}: {Elapsed} (timed out: {TimedOut}, owner: {Owner})",
    info.Filename, info.Elapsed, info.TimedOut, info.Owner);
```

The observer runs on the thread pool once each wait that reached the threshold ends
(acquired or timed out). It never runs on the waiting thread or under internal locks,
and its exceptions are ignored. A wait that never ends shows up in
`CurrentWaiters`/`LongestCurrentWait`, and through the observer once a timeout ends it.
`Owner` in a report is the owner known when that wait began.
