#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/LiteDB.AotSmokeTests/LiteDB.AotSmokeTests.csproj"
runtime_identifier="${RUNTIME_IDENTIFIER:-linux-x64}"
default_output_root="$repo_root/artifacts/aot-feature-parity"
output_root="${AOT_PARITY_OUTPUT_ROOT:-$default_output_root}"
workspace_marker=".litedb-aot-feature-parity-workspace"

if [[ "$output_root" == "$default_output_root" ]]; then
    rm -rf -- "$output_root"
    mkdir -p "$output_root"
else
    if [[ -L "$output_root" || (-e "$output_root" && ! -d "$output_root") ]]; then
        printf '[AOT-PARITY] Refusing unsafe output root: %s must be a directory, not a file or symbolic link.\n' "$output_root" >&2
        exit 1
    fi

    if [[ -d "$output_root" && ! -f "$output_root/$workspace_marker" ]]; then
        unexpected_entry="$(find "$output_root" -mindepth 1 -maxdepth 1 \
            ! -name "$workspace_marker" \
            ! -name regular ! -name regular.log \
            ! -name trimmed ! -name trimmed.log \
            ! -name native-aot ! -name native-aot.log \
            -print -quit)"
        if [[ -n "$unexpected_entry" ]]; then
            printf '[AOT-PARITY] Refusing non-dedicated output root %s because it contains %s.\n' "$output_root" "$unexpected_entry" >&2
            exit 1
        fi
    fi

    mkdir -p "$output_root"
fi

: > "$output_root/$workspace_marker"
rm -rf -- "$output_root/regular" "$output_root/trimmed" "$output_root/native-aot"
rm -f -- "$output_root/regular.log" "$output_root/trimmed.log" "$output_root/native-aot.log"

publish_and_run() {
    local mode="$1"
    shift

    local publish_dir="$output_root/$mode"
    local log="$output_root/$mode.log"

    printf '[AOT-PARITY] Publishing %s mode.\n' "$mode"
    dotnet publish "$project" \
        --configuration Release \
        --runtime "$runtime_identifier" \
        --self-contained true \
        --output "$publish_dir" \
        "$@"

    printf '[AOT-PARITY] Running %s mode.\n' "$mode"
    "$publish_dir/LiteDB.AotSmokeTests" | tee "$log"
}

# The same executable test suite is intentionally used in every mode. Comparing
# its transcript makes adding a scenario to only one publish path impossible.
publish_and_run regular \
    -p:PublishAot=false \
    -p:PublishTrimmed=false
publish_and_run trimmed \
    -p:PublishAot=false \
    -p:PublishTrimmed=true \
    -p:PublishSingleFile=true
publish_and_run native-aot \
    -p:IlcParallelism=1

for mode in trimmed native-aot; do
    if ! diff --unified "$output_root/regular.log" "$output_root/$mode.log"; then
        printf '[AOT-PARITY] FAILED: %s behavior differs from the regular build.\n' "$mode" >&2
        exit 1
    fi
done

printf '[AOT-PARITY] Passed: regular, trimmed, and Native AOT builds completed the identical feature transcript.\n'
