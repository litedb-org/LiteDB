#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/LiteDB.AotSmokeTests/LiteDB.AotSmokeTests.csproj"
runtime_identifier="${RUNTIME_IDENTIFIER:-linux-x64}"
output_root="${AOT_PARITY_OUTPUT_ROOT:-$repo_root/artifacts/aot-feature-parity}"

rm -rf "$output_root"
mkdir -p "$output_root"

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
