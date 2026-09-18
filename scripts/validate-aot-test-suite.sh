#!/usr/bin/env bash
#
# Runs the LiteDB.Tests suite twice through LiteDB.AotTestHost: as a regular JIT application and as a Native AOT
# binary. A test that passes under the JIT and fails as Native AOT must be listed, with its category, in
# LiteDB.AotTestHost/known-aot-differences.tsv; any other such test fails the gate. Tests that fail in both
# modes are environment problems, not AOT differences, and are only reported.
#
#   TARGET_FRAMEWORK     net8.0 (default) or net10.0
#   RUNTIME_IDENTIFIER   defaults to the host
set -euo pipefail
export LC_ALL=C

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/LiteDB.AotTestHost/LiteDB.AotTestHost.csproj"
known_file="$repo_root/LiteDB.AotTestHost/known-aot-differences.tsv"
target_framework="${TARGET_FRAMEWORK:-net8.0}"
executable="LiteDB.Tests"

case "$(uname -s)" in
    Linux*)
        host_os="linux"
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
    *) printf '[AOT-TESTS] Unsupported host: %s\n' "$(uname -s)" >&2; exit 1 ;;
esac

case "$(uname -m)" in
    x86_64 | amd64) host_arch="x64" ;;
    aarch64 | arm64) host_arch="arm64" ;;
    *) printf '[AOT-TESTS] Unsupported architecture: %s\n' "$(uname -m)" >&2; exit 1 ;;
esac

runtime_identifier="${RUNTIME_IDENTIFIER:-$host_os-$host_arch}"
output_root="$repo_root/artifacts/aot-test-suite/$target_framework-$runtime_identifier"

rm -rf -- "$output_root"
mkdir -p "$output_root"

publish_and_run() {
    local mode="$1"
    shift

    printf '[AOT-TESTS] Publishing %s mode (%s, %s).\n' "$mode" "$target_framework" "$runtime_identifier"
    dotnet publish "$project" \
        --configuration Release \
        --runtime "$runtime_identifier" \
        --self-contained true \
        --output "$output_root/$mode" \
        -p:HostTargetFramework="$target_framework" \
        "$@" > "$output_root/$mode.publish.log" 2>&1 || { tail -n 40 "$output_root/$mode.publish.log" >&2; exit 1; }

    printf '[AOT-TESTS] Running %s mode.\n' "$mode"
    # Not piped: a pipeline would report the status of its last command and hide a crash or a timeout.
    local status=0
    "$output_root/$mode/$executable" "$output_root/$mode.tsv" > "$output_root/$mode.run.log" 2>&1 || status=$?
    tail -n 3 "$output_root/$mode.run.log"

    if [ "$status" -ne 0 ] || [ ! -s "$output_root/$mode.tsv" ]; then
        printf '[AOT-TESTS] FAILED: the %s run ended with exit code %s before completing the suite.\n' "$mode" "$status" >&2
        grep -E '^(TIMEOUT|Unhandled)' "$output_root/$mode.run.log" >&2 || tail -n 20 "$output_root/$mode.run.log" >&2
        exit 1
    fi
}

# Test names with the given outcome, sorted for comm.
names() {
    awk -F '\t' -v outcome="$1" '$1 == outcome { print $2 }' "$2" | tr -d '\r' | sort -u
}

publish_and_run regular -p:PublishAot=false
publish_and_run native-aot

cut -f 2 "$output_root/regular.tsv" | tr -d '\r' | sort -u > "$output_root/regular.names"
cut -f 2 "$output_root/native-aot.tsv" | tr -d '\r' | sort -u > "$output_root/native-aot.names"
names Pass "$output_root/regular.tsv" > "$output_root/regular.pass"
names Fail "$output_root/regular.tsv" > "$output_root/regular.fail"
names Fail "$output_root/native-aot.tsv" > "$output_root/native-aot.fail"
grep -v '^#' "$known_file" | tr -d '\r' | awk -F '\t' 'NF >= 2 { print $2 }' | sort -u > "$output_root/known.names"

# Every test the JIT build discovers has to be discovered by the Native AOT build as well.
comm -23 "$output_root/regular.names" "$output_root/native-aot.names" > "$output_root/undiscovered.names"
comm -12 "$output_root/regular.pass" "$output_root/native-aot.fail" > "$output_root/aot-only.names"
comm -23 "$output_root/aot-only.names" "$output_root/known.names" > "$output_root/unexpected.names"
comm -13 "$output_root/aot-only.names" "$output_root/known.names" > "$output_root/stale.names"

# Timing-sensitive tests can fail once for reasons unrelated to AOT: give each unexpected failure one more run.
: > "$output_root/confirmed.names"
while IFS= read -r name; do
    [ -n "$name" ] || continue
    # A crashed retry must not reuse a previous result or be mistaken for a pass.
    : > "$output_root/retry.tsv"
    retry_status=0
    "$output_root/native-aot/$executable" "$output_root/retry.tsv" "$name" > "$output_root/retry.run.log" 2>&1 || retry_status=$?
    if [ "$retry_status" -eq 0 ] && awk -F '\t' -v name="$name" '$2 == name && $1 == "Pass" { found = 1 } END { exit !found }' "$output_root/retry.tsv"; then
        printf '[AOT-TESTS] Flaky, passed on retry: %s\n' "$name"
    else
        printf '%s\n' "$name" >> "$output_root/confirmed.names"
        tail -n 10 "$output_root/retry.run.log"
    fi
done < "$output_root/unexpected.names"

printf '[AOT-TESTS] regular: %s passed. native-aot: %s passed, %s failed, of which %s are known differences.\n' \
    "$(wc -l < "$output_root/regular.pass" | tr -d ' ')" \
    "$(names Pass "$output_root/native-aot.tsv" | wc -l | tr -d ' ')" \
    "$(wc -l < "$output_root/native-aot.fail" | tr -d ' ')" \
    "$(comm -12 "$output_root/aot-only.names" "$output_root/known.names" | wc -l | tr -d ' ')"

if [ -s "$output_root/regular.fail" ]; then
    printf '[AOT-TESTS] JIT baseline failures (not AOT regressions; review alongside ordinary test results):\n'
    awk -F '\t' '$1 == "Fail" { printf "    %s\n        %s\n", $2, $3 }' "$output_root/regular.tsv"
fi

if [ -s "$output_root/stale.names" ]; then
    # Not an error: which generic instantiations have native code can differ between platforms and runtimes.
    printf '[AOT-TESTS] Listed as known differences but not failing here:\n'
    sed 's/^/    /' "$output_root/stale.names"
fi

failed=0

if [ -s "$output_root/undiscovered.names" ]; then
    printf '[AOT-TESTS] FAILED: tests missing from the Native AOT run:\n' >&2
    sed 's/^/    /' "$output_root/undiscovered.names" >&2
    failed=1
fi

if [ -s "$output_root/confirmed.names" ]; then
    printf '[AOT-TESTS] FAILED: tests that pass under the JIT and fail as Native AOT without being listed in %s:\n' "${known_file#"$repo_root"/}" >&2
    while IFS= read -r name; do
        awk -F '\t' -v name="$name" '$2 == name { printf "    %s\n        %s\n", $2, $3 }' "$output_root/native-aot.tsv" >&2
    done < "$output_root/confirmed.names"
    failed=1
fi

[ "$failed" -eq 0 ] || exit 1

printf '[AOT-TESTS] Passed (%s, %s): every Native AOT difference from the JIT run is a documented one.\n' "$target_framework" "$runtime_identifier"
