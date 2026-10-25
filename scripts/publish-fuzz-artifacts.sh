#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 <fuzz-artifact-directory> <label>" >&2
  exit 2
fi

source_dir=$(realpath "$1")
label=$2
if [[ ! -d "$source_dir" ]] || ! find "$source_dir" -name run.json -print -quit | grep -q .; then
  echo "source must contain at least one fuzz run.json" >&2
  exit 2
fi
if [[ ! "$label" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$ ]]; then
  echo "label must contain only letters, digits, dot, underscore, or dash" >&2
  exit 2
fi

publish_temp=$(mktemp -d)
trap 'rm -rf -- "$publish_temp"' EXIT
gh repo clone litedb-org/LiteDB-Artifacts "$publish_temp/repository" -- --depth=1

repository="$publish_temp/repository"
if [[ -n $(git -C "$repository" status --porcelain) ]]; then
  echo "artifact repository clone is unexpectedly dirty" >&2
  exit 1
fi

destination="$repository/fuzzing/$(date -u +%Y-%m-%d)/$label"
if [[ -e "$destination" ]]; then
  echo "artifact destination already exists: ${destination#"$repository/"}" >&2
  exit 1
fi
mkdir -p "$destination"
cp -R "$source_dir"/. "$destination"/

git -C "$repository" add -- "${destination#"$repository/"}"
git -C "$repository" commit -m "Add LiteDB fuzz results $label"
git -C "$repository" push origin HEAD:main
echo "published to fuzzing/$(date -u +%Y-%m-%d)/$label"
