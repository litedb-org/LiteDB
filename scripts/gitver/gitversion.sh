#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
REF="HEAD"
JSON_OUTPUT=false
NO_RESTORE=false
WORKTREE=""
TARGET="$ROOT"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --json)
      JSON_OUTPUT=true
      shift
      ;;
    --no-restore)
      NO_RESTORE=true
      shift
      ;;
    *)
      REF="$1"
      shift
      ;;
  esac
done

cleanup() {
  if [[ -n "$WORKTREE" && -d "$WORKTREE" ]]; then
    git -C "$ROOT" worktree remove --force "$WORKTREE" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

if [[ "$REF" != "HEAD" ]]; then
  SHA=$(git -C "$ROOT" rev-parse --verify --quiet "$REF")
  if [[ -z "$SHA" ]]; then
    echo "Unable to resolve git ref '$REF'" >&2
    exit 1
  fi
  WORKTREE="$(mktemp -d -t litedb-gv-XXXXXX)"
  git -C "$ROOT" worktree add --detach "$WORKTREE" "$SHA" >/dev/null
  TARGET="$WORKTREE"
else
  SHA=$(git -C "$ROOT" rev-parse HEAD)
fi

cd "$TARGET"

if [[ "$NO_RESTORE" != "true" ]]; then
  dotnet tool restore >/dev/null
fi

JSON=$(dotnet tool run dotnet-gitversion /output json)

if [[ "$JSON_OUTPUT" == "true" ]]; then
  printf '%s\n' "$JSON"
  exit 0
fi

MAJOR_MINOR_PATCH=$(jq -r '.MajorMinorPatch' <<<"$JSON")
PRE_LABEL=$(jq -r '.PreReleaseLabel' <<<"$JSON")
PRE_NUMBER=$(jq -r '.PreReleaseNumber' <<<"$JSON")
SHORT_SHA=$(jq -r '.ShortSha' <<<"$JSON")
BRANCH=$(jq -r '.BranchName' <<<"$JSON")

SEMVER="$MAJOR_MINOR_PATCH"
if [[ "$PRE_LABEL" != "" && "$PRE_LABEL" != "null" ]]; then
  printf -v PRE_PADDED '%04d' "$PRE_NUMBER"
  SEMVER="${MAJOR_MINOR_PATCH}-${PRE_LABEL}.${PRE_PADDED}"
fi

printf '%-22s %s\n' "Resolved SHA:" "$SHA"
printf '%-22s %s\n' "FullSemVer:" "$SEMVER"
printf '%-22s %s\n' "NuGetVersion:" "$SEMVER"
printf '%-22s %s\n' "Informational:" "${SEMVER}+${SHORT_SHA}"
printf '%-22s %s\n' "BranchName:" "$BRANCH"