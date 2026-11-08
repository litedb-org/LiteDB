# Synchronous handle lifetime

A live LiteDatabase retains every active handle. Dropping a handle reference or
ending its creator thread does not roll it back. Dispose the handle or database
for deterministic release; an abandoned handle can otherwise reserve the writer
indefinitely (including read-only Shared handles).

When the complete database/handle graph becomes unreachable, file-backed Direct
facades enqueue host cleanup and Shared facades enqueue holder cleanup from their
finalizers. Normal threads roll back storage and release native admission; the
finalizer thread never waits on the native mutex. GC/reaper cleanup has no
promptness guarantee. Caller-owned streams retain their existing ownership
contract: explicit database disposal is required.

Database close refuses new ordinary and handle calls, cancels pending begins,
rolls back idle handles, and drains executing calls before releasing storage.
Close from the handle's own callback throws before changing close state. Close
can wait for application callbacks indefinitely. A begin published before close
is owned and settled by close; a begin still pending when close wins throws
ObjectDisposedException after releasing its acquisition. Bound readers are
invalidated and drained before rollback. With disposeOnClose:false, database
handles still roll back while the supplied engine remains caller-owned.

Maintenance never rolls back a handle to gain exclusivity: it times out or
refuses unsupported recursion/multiple Direct facades. Final native admission
retirement follows resource cleanup. A process kill releases OS ownership;
next open discards unconfirmed spilled WAL and retains committed indexed rows.
This is a process-death contract, not a model of power loss.
