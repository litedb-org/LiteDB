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
  explorer, which needs the PR #133 handle API that `dev` does not have. Each
  adapter has an `adapter.json` and a `README.md`.
- `smoke/`: the `harness-smoke` self-check (a patch and a check script). It is
  **not a net**. It proves only that the worktree, overlay, build, run and
  classify pipeline works.

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
