#!/usr/bin/env bash
#
# Publishes LiteDB.AotSmokeTests as a regular, a trimmed, and a Native AOT application, runs all three, and
# requires byte-identical transcripts. Any trim or AOT diagnostic (ILxxxx) in any publish log fails the gate,
# whatever its code or severity. A fourth Native AOT publish roots the whole LiteDB assembly so the compiler
# analyses every method of the library and not only the code the smoke scenarios reach.
#
#   TARGET_FRAMEWORK     net8.0 (default) or net10.0
#   RUNTIME_IDENTIFIER   defaults to the host: linux-x64, linux-arm64, linux-musl-x64, osx-arm64, win-x64, ...
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/LiteDB.AotSmokeTests/LiteDB.AotSmokeTests.csproj"
target_framework="${TARGET_FRAMEWORK:-net8.0}"
executable="LiteDB.AotSmokeTests"

case "$(uname -s)" in
    Linux*)
        host_os="linux"
        # Alpine and other musl distributions need their own runtime identifier.
        if [ -f /etc/alpine-release ] || (ldd --version 2>&1 || true) | grep -qi musl; then
            host_os="linux-musl"
        fi
        ;;
    Darwin*) host_os="osx" ;;
    MINGW* | MSYS* | CYGWIN*)
        host_os="win"
        executable="$executable.exe"
        # The Native AOT targets locate the MSVC linker through vswhere.exe, which Git Bash does not have on PATH.
        PATH="$PATH:/c/Program Files (x86)/Microsoft Visual Studio/Installer"
        ;;
    *) printf '[AOT-PARITY] Unsupported host: %s\n' "$(uname -s)" >&2; exit 1 ;;
esac

case "$(uname -m)" in
    x86_64 | amd64) host_arch="x64" ;;
    aarch64 | arm64) host_arch="arm64" ;;
    *) printf '[AOT-PARITY] Unsupported architecture: %s\n' "$(uname -m)" >&2; exit 1 ;;
esac

runtime_identifier="${RUNTIME_IDENTIFIER:-$host_os-$host_arch}"
output_root="$repo_root/artifacts/aot-feature-parity/$target_framework-$runtime_identifier"

rm -rf -- "$output_root"
mkdir -p "$output_root"

publish() {
    local mode="$1"
    shift

    local publish_log="$output_root/$mode.publish.log"

    printf '[AOT-PARITY] Publishing %s mode (%s, %s).\n' "$mode" "$target_framework" "$runtime_identifier"
    dotnet publish "$project" \
        --configuration Release \
        --runtime "$runtime_identifier" \
        --self-contained true \
        --output "$output_root/$mode" \
        -p:SmokeTargetFramework="$target_framework" \
        "$@" 2>&1 | tee "$publish_log"

    # The ILCompiler targets have no warnings-as-errors switch and the csproj can only list known codes, so the
    # log is the one place that sees every trim and AOT diagnostic, including codes added by future SDKs.
    # Under pipefail the pipeline fails when grep matches nothing, so the branch runs only when diagnostics exist.
    if grep -E '(warning|error) IL[0-9]{4}' "$publish_log" | sort -u > "$output_root/$mode.il-diagnostics.log"; then
        printf '[AOT-PARITY] FAILED: %s publish produced trim or AOT diagnostics:\n' "$mode" >&2
        cat "$output_root/$mode.il-diagnostics.log" >&2
        exit 1
    fi
}

run() {
    local mode="$1"

    printf '[AOT-PARITY] Running %s mode.\n' "$mode"
    "$output_root/$mode/$executable" | tee "$output_root/$mode.log"
}

# The same executable test suite is intentionally used in every mode. Comparing
# its transcript makes adding a scenario to only one publish path impossible.
publish regular -p:PublishAot=false -p:PublishTrimmed=false
run regular
publish trimmed -p:PublishAot=false -p:PublishTrimmed=true -p:PublishSingleFile=true
run trimmed
publish native-aot -p:IlcParallelism=1
run native-aot
publish native-aot-whole-library -p:IlcParallelism=1 -p:LiteDbAnalyzeWholeLibrary=true
run native-aot-whole-library

for mode in trimmed native-aot native-aot-whole-library; do
    if ! diff -u "$output_root/regular.log" "$output_root/$mode.log"; then
        printf '[AOT-PARITY] FAILED: %s behavior differs from the regular build.\n' "$mode" >&2
        exit 1
    fi
done

printf '[AOT-PARITY] Passed (%s, %s): no trim or AOT diagnostics, and the regular, trimmed, Native AOT, and whole-library Native AOT builds produced the identical transcript.\n' "$target_framework" "$runtime_identifier"
