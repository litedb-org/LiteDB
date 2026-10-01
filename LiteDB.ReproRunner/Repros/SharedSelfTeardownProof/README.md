# Same-instance Shared teardown proof support

Shared production-only sources for `SharedSelfTeardownRelease` and
`SharedSelfTeardownWait`. Neither test-only engine hooks nor changed timeouts are
used. Each plain/encrypted scenario runs in a separate child process, with its
original-volume database, WAL, stdout and stderr retained in the printed
`self-teardown-*` directory.

A `FOR UPDATE` reader retains the native writer mutex. Sixty committed large
rows force a data-stream checkpoint callback when that reader retires. The
callback reenters the same raw `SharedEngine` through either a pragma getter or
`Dispose`.

* Release proof: a fresh foreign thread cannot acquire the actual native mutex
  before nested disposal but can acquire it before the callback returns. This is
  the defect witness. No corruption claim or cleanup on the resulting unsafe
  graph is required. Known-bad files remain untouched after the observed result.
* Wait proof: the callback remains active, its thread is blocked, the connection
  still publishes its closing core, and that core's exclusive operation owner
  is the very same callback thread. A foreign thread independently confirms the
  native mutex remains excluded. The entire witness must hold twice, 50 ms
  apart. The child exits with its blocked background worker; it does not dispose
  the graph or inspect live files. A watchdog timeout alone always fails.

The fixed result requires the exact documented refusal and unchanged native
exclusion inside the callback, followed by another facade's write and two cold
reopens verifying every document payload, indexed lookup (including index-seek
plan), and unrelated sentinel. Positive controls require ordinary same-instance
getter recursion and other-file callback access to succeed, with the same cold
state checks. Both streams use the production Darwin native-handle pattern when
that API exists, avoiding runtime whole-file locks masking native ownership.

The parent drains and retains both child output streams concurrently, validates
child exit code and semantic marker, rejects stderr or mixed outcomes, and kills
an unexpectedly stuck child after 30 seconds. Killing is diagnostic containment,
never proof of a defect. This is process termination, not modeled power loss.
