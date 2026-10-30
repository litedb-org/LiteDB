# Issue 3082: allocation publication

A reserves pages in an explicit transaction, B acknowledges a header commit,
and A exits idle without committing or rolling back. The three layouts cover
an extension hole, an unwritten tail and a reserved existing free page, each in
plain and encrypted files. Disposal never overlaps an executing facade call.

After ordinary recovery/checkpoint, the public-API document/index oracle checks
complete committed documents and the selected secondary-index plan. An independent
raw-byte oracle checks page identity, physical length, collection ownership and
exact empty-page/free-list membership. No repair or header clamping is performed.

The package variant pins published `6.0.0-prerelease.319`, which reproduces all six
cases. Exit 0 reports the structural bug; the fixed source exits 1. The permanent
xUnit guards additionally cover SIGKILL, physical lost/torn writes, node/block
reachability, safepoints, multiple owners, repeated recovery and allocation reuse.
