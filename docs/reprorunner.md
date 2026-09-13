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

### Job layout

1. **generate-matrix**
   - Checks out the requested ref.
   - Restores/builds the CLI and captures the JSON inventory: `reprorunner list --json [--filter <regex>]`.
   - Loads `.github/os-matrix.json`, applies each repro's constraints, and emits a matrix of `{ os, repro }` pairs.
   - Writes a summary of scheduled/skipped repros (with reasons) to `$GITHUB_STEP_SUMMARY`.
   - Uploads `repros.json` for debugging.

2. **repro**
   - Runs once per matrix entry using `runs-on: ${{ matrix.os }}`.
   - Builds the CLI in Release mode.
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
  or in the CI job artifacts prefixed with `logs-`.

When crafting new repro manifests, prefer `supports` for broad platform gating and the `os` block for
precise runner pinning.

## Troubleshooting matrix expansion

- **Repro skipped unexpectedly** – run `reprorunner show <name>` to confirm the declared OS metadata.
  Verify the values match the keys in `.github/os-matrix.json`.
- **Unknown platform/label warnings** – the manifest references a runner that is not present in the OS
  matrix. Update the manifest or add the missing label to `.github/os-matrix.json`.
- **Empty workflow after filtering** – double-check the `filter` regex and ensure the CLI discovers at
  least one repro whose name matches the expression.


