# harness-smoke adapter

Harness self-check only, not a net. `net_proof.py run --id harness-smoke` copies
`marker.txt` into `net-proof-smoke/` of each tree under test; the
`net-proof-smoke` capability probe then sees it on the overlaid tree, which
shows that adapters are applied before capabilities are checked.
