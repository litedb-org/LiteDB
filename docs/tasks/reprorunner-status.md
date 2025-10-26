# TASK: Implement OS-aware dynamic matrix for reprorunner in GitHub Actions

## Goal

Each reprorunner **repro** should automatically fan out into its own CI jobs across the repo’s supported **Windows and Linux** runners, with **no CI edits** required when a developer adds a new repro. Repros can declare their supported OSes; the matrix must respect that.

## Acceptance criteria

1. A new workflow exists at **`.github/workflows/reprorunner.yml`** with two jobs:

   * `generate-matrix`: builds a **dynamic** matrix by combining:

     * the repository’s OS labels (single source of truth), and
     * `reprorunner list --json` output (repro inventory + OS constraints).
   * `repro`: runs one job per `{ os, repro }` using `runs-on: ${{ matrix.os }}`, executes `reprorunner run <name> --ci`, uploads logs/artifacts, and writes a short summary.
2. Repro OS constraints are honored:

   * **Simple**: `supports: ["windows" | "linux" | "any"]` (default = `"any"` if omitted).
   * **Advanced**:

     ```json
     "os": {
       "includePlatforms": ["windows","linux"],
       "includeLabels": ["windows-2022","ubuntu-22.04"],
       "excludePlatforms": ["linux"],
       "excludeLabels": ["ubuntu-24.04"]
     }
     ```
   * The final job set for a repro is the intersection of its constraints with the repo’s OS labels. If empty, skip the repro and emit a warning in the matrix job summary.
3. The repo’s OS labels are defined **once** and shared by all workflows:

   * Preferred: `.github/os-matrix.json`:

     ```json
     { "linux": ["ubuntu-22.04","ubuntu-24.04"], "windows": ["windows-2022"] }
     ```
   * (Optional) Instead, add a harvester step that parses existing workflows for `runs-on` values to avoid any duplication.
4. The workflow supports `workflow_dispatch` inputs:

   * `filter` (optional regex) to run only matching repros,
   * `ref` (optional branch/SHA) to run against a specific ref.
5. Artifacts are uploaded per job, named `logs-<repro>-<os>`. `$GITHUB_STEP_SUMMARY` contains a short per-job summary.
6. Documentation added (e.g., `docs/reprorunner.md`): explains `list --json` schema, OS constraints, local run instructions, and how the CI expansion works.

## Implementation outline

1. **reprorunner CLI contract**

   * `list --json [--filter <regex>]` prints:

     ```json
     {
       "repros": [
         { "name": "FastInsert", "supports": ["any"] },
         { "name": "WindowsOnly", "supports": ["windows"] },
         { "name": "PinnedUbuntu", "os": { "includeLabels": ["ubuntu-22.04"] } }
       ]
     }
     ```
   * `run <name> --ci` executes the repro; non-zero exit on failure; logs to `artifacts/`.
   * (Optional) accept `--target-os <runner-label>` to inform the repro which runner label it’s on.
2. **Workflow: `.github/workflows/reprorunner.yml`**

   * `generate-matrix` job (ubuntu):

     * Checkout → setup .NET → build reprorunner.
     * Run `reprorunner list --json [...]` → `repros.json`.
     * Load OS labels (from `.github/os-matrix.json` **or** harvester) → `os.json`.
     * **Compose** `matrix.json` with `include: [ { os, repro }, ... ]` by intersecting constraints with available labels.
     * Output `matrix` + `count`; print warnings to summary if any repros match nothing.
   * `repro` job:

     * `runs-on: ${{ matrix.os }}`, matrix from `fromJSON(needs.generate-matrix.outputs.matrix)`.
     * Checkout → setup .NET → build → `reprorunner run ${{ matrix.repro }} --ci --target-os "${{ matrix.os }}"`.
     * Upload artifacts; write summary.
3. **Single source of truth for OS labels**

   * Add `.github/os-matrix.json` and have other workflows consume it too; or implement the harvester so you never duplicate OS labels anywhere.
4. **Docs**

   * Add examples showing how `supports` and `os` constraints map to actual jobs.

## Test plan

* Create three sample repros:

  1. `AnyRepro` (no constraints) → runs on all repo OS labels.
  2. `WindowsOnly` (`supports: ["windows"]`) → runs only on Windows labels.
  3. `PinnedUbuntu` (`os.includeLabels: ["ubuntu-22.04"]`) → runs only on 22.04 even if 24.04 exists.
* Trigger workflow with:

  * No filter (all repros),
  * `filter=Windows` (subset),
  * A specific `ref`.
* Verify the generated matrix’s size and contents; confirm artifacts/summaries appear; confirm failures mark jobs red.

## Definition of Done

* Workflow added, green on branch with a fan-out run visible.
* Adding a new repro (class/case) requires **no** CI edits and is auto-discovered.
* OS list is centralized; other workflows either read the same file or are harvested automatically.
* Docs committed and reviewed.



# ReproRunner CI Refresh – Status

## Summary

- Added `.github/os-matrix.json` as the single source for runner labels.
- Replaced the inline Python matrix builder with `.github/scripts/compose_repro_matrix.py`.
- Created `.github/workflows/reprorunner.yml` that now supports `workflow_call` and `workflow_dispatch`.
- Wired `ci.yml` to invoke the new workflow after the reusable build/test step.
- Removed the legacy repro job from `_reusable-ci.yml`.
- Documented the new behavior in `docs/reprorunner.md`.

## Remaining Actions

1. `git add` + `git commit` + `git push` (not yet run).
2. Monitor GitHub Actions (`ci.yml`) to confirm the `Repro Runner` job completes successfully.

## Notes

Current working tree contains the updated workflow plus the new helper script and documentation. No tests executed locally; waiting on CI once the branch is pushed.***
