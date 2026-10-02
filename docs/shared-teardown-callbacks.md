# Shared callbacks during core teardown

Closing a Shared storage core can call user-owned streams while the connection
still retains the native writer mutex. Those callbacks must not reenter the same
connection: its old core is still checkpointing or releasing storage resources.
Ordinary same-connection calls outside teardown remain supported, and a callback
can still access a different database.

`SharedCallFrames` marks retained-core teardown separately from ordinary operation
recursion. Public engine calls and raw disposal reject reentry before opening a
replacement core or releasing writer ownership. Already-disposed connections keep
their existing getter refusal and repeated-disposal behavior. The teardown frame
ends only when the actual close returns; a core's disposed flag is not a teardown
completion signal.

The guard covers reader-retained cores, pin-holder close, the last reader's final
checkpoint, connection close/checkpoint, abandoned-owner cleanup and cleanup of a
core returned from construction. It does not claim to cover callbacks during
failure cleanup inside a constructor before that core has been returned.

## Regression evidence

[`SharedSelfCloseCallback_Tests`](../LiteDB.Tests/Engine/SharedSelfCloseCallback_Tests.cs)
forces five close routes in plain and encrypted databases, tests getter and raw
disposal callbacks, and probes the real native writer mutex from a foreign thread
before and after attempted reentry. It also requires no replacement core to open,
ordinary same-connection recursion and other-database access to succeed, a later
writer to progress, and exact indexed documents and sentinel values on two cold
reopens. Existing peer-callback tests remain unchanged.

The production-only [`SharedSelfTeardownRelease`](../LiteDB.ReproRunner/Repros/SharedSelfTeardownRelease/README.md)
proof runs against published package `6.0.0-prerelease.319` and the current source.
The known-bad result requires actual native acquisition inside the callback after
nested disposal. The isolated child exits immediately at that witness so the
unprotected checkpoint does not continue. A timeout is never successful proof.
The fixed result requires the exact refusal, ownership retained inside the callback,
positive controls, later writer progress, and repeated cold integrity checks.

This defect predates transaction handles. A separate manifestation in JKamsker/LiteDB#133
waits on its own closing-core fence; the upstream proof intentionally targets the
published early-release behavior. These tests establish process and exception
behavior, not power-loss or physical-device guarantees. They do not expand
transaction, admission, batching, cache, or durability functionality.
