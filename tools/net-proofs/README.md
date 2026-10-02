# Net-proof overlays

Source that `.github/scripts/net_proof.py` copies or applies into a known-bad or
fix tree before it builds and runs a net. Nothing here is built with this
repository: no project references these files, they are not in `LiteDB.sln`, and
SDK-style projects only compile files below their own directory. See
[Net proofs](../../docs/rules/safety-evidence.md#net-proofs) and
`.github/safety/net-proofs.json`.

## Layout

- `adapters/<name>/`: an **adapter**, code that compiles only against the trees it
  is written for. One example is a transaction-handle access adapter for the
  explorer, which needs the handle API of the fork PR
  [JKamsker/LiteDB#133](https://github.com/JKamsker/LiteDB/pull/133) that `dev` does not have. Each
  adapter has an `adapter.json` and a `README.md`.
- `smoke/`: the `harness-smoke` self-check (a patch and a check script). It is
  **not a net**. It proves only that the worktree, overlay, build, run and
  classify pipeline works.

## Adapters

Every adapter below backs recorded entries of the ledger (`net.recorded.overlay` names the tree,
`recordedOverlays` in the ledger lists which adapters it carried). Applying one by hand is described in
its README.

| Adapter | Applied by | Used for | Label |
| --- | --- | --- | --- |
| `harness-smoke` | copy | the pipeline self-check | not a net |
| `transaction-handle` | copy | explorer `handle` access kind and handle-only scenarios on fork trees | frozen with the nets |
| `pbt-handle` | copy + `register-handle-kind.patch` (+ `replay-overlay.patch`) | parallel property test `handle` access kind (rows 5, 12, 14) | frozen; its fail-fast options are tuned (see its README) |
| `waitgraph-pr133` | `run.sh` (per-commit site patches) | wait-for graph on fork trees (rows 3, 4, 12, 13) | frozen |
| `pr133-coverage` | `git apply pr133-coverage.patch` | coverage correction on replay trees (V-A2, later fixes) | harness configuration; masks the #3071 class where #3072 is missing |
| `row5-transition` | copy (with `transaction-handle`) | row 5 transition check | post-freeze, tuned-after-fix |
| `row8-crash-leftovers` | `git apply row8-scratch.patch` | row 8 crash-leftover prior and spilled-reader kind | crash-leftover prior tuned-after-fix |

An adapter whose files must be patched into existing sources (rather than copied) ships a `.patch`
and names an inert `target` (`net-proof-<name>/`), so `net_proof.py` can still copy it and probe it.

## `adapter.json`

```json
{
  "name": "transaction-handle",
  "description": "What the adapter adds and what it drives",
  "target": "LiteDB.Tests/NetProofAdapters/TransactionHandle",
  "requires": ["handle-api"]
}
```

- `target` is a relative directory inside the tree under test. Every file of the
  adapter directory except `adapter.json` and `README.md` is copied there, with
  its relative path kept. A target below a test project's directory (for example
  `LiteDB.Tests/...`) is compiled by that project.
- `requires` names capabilities from the ledger's capability table that the
  *plain* tree must already have. When one is missing, the adapter is not copied
  and the proof is `not-applicable` with those names.

An entry uses an adapter by listing its directory in `net.overlay.adapters`. It
also lists the capabilities the net needs after the overlay in `net.requires`,
and usually adds a probe that recognizes the copied adapter.

## Rules

- C# adapter files follow the repository's style and size rules:
  `scripts/check-csharp-size.py` checks them like any other changed `.cs` file
  (300 lines recommended, 500 hard limit).
- Never reference an adapter from a project in this repository. It must not
  compile against `dev`.
- An adapter must not encode the defect it is used to detect. Drive the public
  surface (the handle API's operations), not the fix.
