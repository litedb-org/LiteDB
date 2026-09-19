# Versioning

LiteDB uses GitVersion for semantic versioning across local builds and CI. The configuration lives in `GitVersion.yml` and is consumed by both MSBuild (via `GitVersion.MsBuild`) and the GitHub workflows.

> [!NOTE]
> Environments that expose the repository as a detached worktree or otherwise hide the `.git/HEAD` sentinel (for example, some test harnesses) automatically fall back to a static `0.0.0-detached` version so builds can proceed without GitVersion.

## Branch semantics

- `master` is the mainline branch. Each direct commit or merge increments the patch number unless an annotated `v*` tag (or `+semver:` directive) requests a larger bump.
- `dev` tracks the next patch version and produces prerelease builds like `6.0.1-prerelease.0003`. The numeric suffix is zero-padded for predictable ordering.
- Feature branches (`feature/*`, `bugfix/*`, `chore/*`, `refactor/*`, `pr/*`) inherit their base version but do not publish artifacts. They exist purely for validation.

The first prerelease that precedes the 6.0.0 release (commit `a0298891ddcaf7ba48c679f1052a6f442f6c094f`) remains the baseline for the prerelease numbering history.

## GitHub workflows

- `publish-prerelease.yml` runs on every push to `dev`. It resolves the semantic version with GitVersion, runs the full test suite, packs the library, and pushes the resulting prerelease package to NuGet. GitHub releases are intentionally skipped for now.
- `publish-release.yml` is manual (`workflow_dispatch`). It computes the release version and can optionally push to NuGet and/or create a GitHub release via boolean inputs. GitHub releases use a zero-padded prerelease counter for predictable sorting in the UI, while NuGet publishing keeps the standard GitVersion output. By default it performs a dry run (build + pack only) so we keep the publishing path disabled until explicitly requested.
- `tag-version.yml` lets you start a manual major/minor/patch bump. It tags the specified ref (defaults to `master`) with the next `v*` version so future builds pick up the new baseline. Use this after validating a release candidate.

## Dry-running versions

GitVersion is registered as a local dotnet tool. Restore the tool once (`dotnet tool restore`) and use one of the helpers:

```powershell
# PowerShell (Windows, macOS, Linux)
./scripts/gitver/gitversion.ps1            # show version for HEAD
./scripts/gitver/gitversion.ps1 dev~3      # inspect an arbitrary commit
./scripts/gitver/gitversion.ps1 -Json      # emit raw JSON
```

```bash
# Bash (macOS, Linux, Git Bash on Windows)
./scripts/gitver/gitversion.sh             # show version for HEAD
./scripts/gitver/gitversion.sh dev~3       # inspect an arbitrary commit
./scripts/gitver/gitversion.sh --json      # emit raw JSON
```

Both scripts resolve the git ref to a SHA, execute GitVersion with the repository configuration, and echo the key fields (FullSemVer, NuGetVersion, InformationalVersion, BranchName).

## Manual bumps

1. Merge the desired changes into `master`.
2. Run the **Tag version** workflow from the Actions tab, pick `master`, and choose `patch`, `minor`, or `major`.
3. The workflow creates and pushes the annotated `v*` tag. The next prerelease build from `dev` will increment accordingly, and the next stable run from `master` will match the tagged version.

`+semver:` commit messages are still honoured. For example, including `+semver: minor` in a commit on `master` advances the minor version even without a tag.

## Working locally

- `dotnet build` / `dotnet pack` automatically consume the GitVersion-generated values; no extra parameters are required.
- To bypass GitVersion temporarily (e.g., for experiments), set `GitVersion_NoFetch=false` in the build command. Reverting the property restores normal behaviour.
- When you are ready to publish a prerelease, push to `dev` and let the workflow take care of packing and nuget push.

For historical reference, the `v6.0.0-prerelease.0001` tag remains anchored to commit `a0298891ddcaf7ba48c679f1052a6f442f6c094f`, ensuring version ordering continues correctly from the original timeline.

