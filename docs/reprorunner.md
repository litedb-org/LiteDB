# ReproRunner CI and JSON Contract

The ReproRunner CLI discovers, validates, and executes LiteDB reproduction projects. This document
captures the machine-readable schema emitted by `list --json`, the OS constraint syntax consumed by
CI, and the knobs available to run repros locally or from GitHub Actions.

## JSON inventory contract

Running `reprorunner list --json` (or `dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- list --json`)
produces a stable payload with one entry per repro:

```json
{
  "repros": [
    {
      "name": "AnyRepro",
      "supports": ["any"]
    },
    {
      "name": "WindowsOnly",
      "supports": ["windows"]
    },
    {
      "name": "PinnedUbuntu",
      "os": {
        "includeLabels": ["ubuntu-22.04"]
      }
    }
  ]
}
```

The top level includes:

- `repros` – array of repro descriptors.
- Each repro has a unique `name` (matching the manifest id).
- `supports` – optional list describing the broad platform family. Accepted values are `windows`,
  `linux`, and `any`. Omitted or empty means `any`.
- `os` – optional advanced constraints that refine the supported runner labels.

### Advanced OS constraints

The `os` object supports four optional arrays. Each entry is compared in a case-insensitive manner
against the repository's OS matrix.

```json
"os": {
  "includePlatforms": ["linux"],
  "includeLabels": ["ubuntu-22.04"],
  "excludePlatforms": ["windows"],
  "excludeLabels": ["ubuntu-24.04"]
}
```

Resolution rules:

1. Start with the labels implied by `supports` (`any` => all labels).
2. Intersect with `includePlatforms` (if present) and `includeLabels` (if present).
3. Remove any labels present in `excludePlatforms` and `excludeLabels`.
4. The final set is intersected with the repo-level label inventory. If the result is empty, the repro
   is skipped and the CI generator prints a warning.

Unknown platforms or labels are ignored for the purposes of scheduling but are reported in the matrix
summary so the manifest can be corrected.

## Centralised OS label inventory

Supported GitHub runner labels live in `.github/os-matrix.json` and are shared across workflows:

```json
{
  "linux": ["ubuntu-22.04", "ubuntu-24.04"],
  "windows": ["windows-2022"]
}
```

When a new runner label is added to the repository, update this file and every workflow (including
ReproRunner) picks up the change automatically.

## New GitHub Actions workflow

`.github/workflows/reprorunner.yml` drives ReproRunner executions on CI. It offers two entry points:

- Manual triggers via `workflow_dispatch`.
- Automatic execution via `workflow_call` from the main `ci.yml` workflow.
- Optional inputs:
  - `filter` - regular expression to narrow the repro list.
  - `ref` - commit, branch, or tag to check out.
  - `tier` - `pr` selects one eligible image per platform; `full` selects all eligible images.
  - `include-regressions` - manual dispatch only; run historical repros retired in favor of normal tests.

The main CI workflow on ordinary PRs runs the complete test suite on Linux .NET 8/.NET 10, Windows .NET 10,
and Windows .NET Framework 4.8.1: six jobs including the two builds. They do not
start ReproRunner's inventory or execution jobs. Separate migration compatibility
and path-triggered fuzz/performance workflows retain their own jobs. The `full-ci` label opts into
full coverage; pushes to `dev` and nightly CI also run the full tier. Full tests
retain verified .NET 9 runtimes, x86, ARM64, macOS, and the second Windows image.
Shared-process tests run in the normal suite; x86 diagnostics and repeated insert
checks share the Windows test job instead of duplicating the matrix.

### Retiring fixed repros

`.github/repro-ci.json` lists repros replaced by named regression tests. Both
automatic tiers omit these entries. The matrix builder validates the referenced
test methods; its policy tests run inside the existing Linux build check. New
repros remain enabled by default in full CI. Use `full-ci` on PRs that need them.
Do not retire a repro solely because its manifest says `green`: first establish
that normal tests cover the relevant behavior and failure paths.

| Repro | Automatic CI | Replacement / reason |
| --- | --- | --- |
| `Issue_2586_RollbackTransaction` | Disabled | `Transactions_Tests` covers dirty/read-only safepoint buffers; `Issue2586_RollbackSafety_Tests` adds committed rows, indexed reads, subsequent writes, and file reopen. |
| `Issue_2614_DiskServiceDispose` | Disabled | `Issue2614_InitializationCleanup_Tests` injects EIO/ENOSPC during new-header sync, checks exclusive reopen before GC, repeats failures, then writes/reopens successfully; `StreamOwnership_Tests` covers caller-owned streams. |
| `Issue_3071_SharedPeerCallback` | Disabled | `SharedPeerCallback_Tests`, `SharedPeerReaderCallback_Tests` and `SharedPeerCloseCallback_Tests` cover every retaining route and close path plain and encrypted, with same-connection, other-database, leased-reader and idle-pin controls; the historical package comparison runs as its regression proof. |
| `Issue_2561_TransactionMonitor` | Full tier | Still marked `red`: verifies a known-bug reproduction, not a passing fixed-bug guard. |

The disk replacement tests exercise constructor cleanup through deterministic I/O
fault injection on real files, without changing the test process's resource limits.
The historical RLIMIT_FSIZE/package comparison remains available locally and via
manual `include-regressions`; it is not a claim of general power-loss coverage.
Storage compatibility scripts and the normal recovery/fault-injection suites remain
in CI. No storage implementation changes are needed for this consolidation.

### Regression proofs

A fixed (`green`) repro can prove its fix against a real known-bad state: a
published package, a `dev` commit or an originating-PR commit, never a mutant.
`.github/safety/regression-proofs.json` pins the state and names the permanent
regression guard. The **Regression proof** workflow requires the known-bad package
variant to reproduce and the candidate source to pass, on the PR and once more on
the merged `dev` revision; after that it runs only when the proof, its repro or
the proving harness changes. A bug-fix PR (labelled `bug` or `bugfix-fix`) must
add at least one proof; `python .github/scripts/regression_proof.py new ...`
scaffolds the repro and its entry. See
[safety evidence](rules/safety-evidence.md#regression-proofs).

### Job layout

1. **generate-matrix**
   - Checks out the requested ref.
   - Restores/builds the CLI and captures the JSON inventory: `reprorunner list --json [--filter <regex>]`.
   - Loads `.github/os-matrix.json`, excludes retired repros unless manually requested, applies OS constraints and the tier, and emits `{ os, repro }` pairs.
   - Writes a summary of scheduled/skipped repros (with reasons) to `$GITHUB_STEP_SUMMARY`.
   - Uploads `repros.json` for debugging and packages the CLI for all matrix jobs.

2. **repro**
   - Runs once per matrix entry using `runs-on: ${{ matrix.os }}`.
   - Downloads and extracts the CLI built once by `generate-matrix`.
   - Executes `reprorunner run <name> --ci --target-os "<runner label>"`.
   - Uploads `logs-<repro>-<os>` artifacts (`artifacts/` plus the CLI `runs/` folder when present).
   - Appends a per-job summary snippet (status + artifact hint).

The `repro` job is skipped automatically when no repro qualifies after constraint evaluation.

## Running repros locally

Most local workflows mirror CI:

- List repros (optionally filtered):

  ```bash
  dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- list --json --filter Fast
  ```

- Execute a repro under CI settings (for example, on Windows):

  ```bash
  dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -- \
    run Issue_2561_TransactionMonitor --ci --target-os windows-2022
  ```

- View generated artifacts under `LiteDB.ReproRunner/LiteDB.ReproRunner.Cli/bin/<tfm>/<configuration>/runs/...`
  or in the CI job artifacts prefixed with `logs-` (the packaged CLI writes to `reprorunner-cli/runs`).

When crafting new repro manifests, prefer `supports` for broad platform gating and the `os` block for
precise runner pinning.

## Troubleshooting matrix expansion

- **Repro skipped unexpectedly** – run `reprorunner show <name>` to confirm the declared OS metadata.
  Verify the values match the keys in `.github/os-matrix.json`.
- **Unknown platform/label warnings** – the manifest references a runner that is not present in the OS
  matrix. Update the manifest or add the missing label to `.github/os-matrix.json`.
- **Empty workflow after filtering** – double-check the `filter` regex and ensure the CLI discovers at
  least one repro whose name matches the expression.


