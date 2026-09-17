#!/usr/bin/env bash
#
# Builds, installs, and runs the checked-in .NET iOS AOT smoke application.
#
# Usage:
#   ./scripts/validate-ios-aot.sh simulator
#   IOS_DEVICE_ID=<udid> IOS_CODESIGN_KEY='Apple Development: ...' \
#     ./scripts/validate-ios-aot.sh device-mono
#   IOS_DEVICE_ID=<udid> IOS_CODESIGN_KEY='Apple Development: ...' \
#     ./scripts/validate-ios-aot.sh device-nativeaot
#   IOS_DEVICE_ID=<udid> IOS_CODESIGN_KEY='Apple Development: ...' \
#     ./scripts/validate-ios-aot.sh device-all
#
# Optional environment:
#   IOS_SIMULATOR_ID             simulator UDID; a booted/available iPhone is selected otherwise
#   IOS_CODESIGN_PROVISION       defaults to Automatic
#   IOS_VALIDATE_XCODE_VERSION   defaults to true; set false only for a deliberate version override
#   DEVELOPER_DIR                selects a non-default Xcode installation
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/integration/LiteDB.iOSAotSmoke/LiteDB.iOSAotSmoke.csproj"
mode="${1:-simulator}"
bundle_id="org.litedb.iosaotsmoke"

fail() {
    printf '[IOS-AOT] FAILED: %s\n' "$1" >&2
    exit 1
}

require_command() {
    command -v "$1" > /dev/null || fail "Required command '$1' was not found."
}

select_simulator() {
    if [ -n "${IOS_SIMULATOR_ID:-}" ]; then
        printf '%s\n' "$IOS_SIMULATOR_ID"
        return
    fi

    xcrun simctl list devices available -j | python3 -c '
import json, sys
data = json.load(sys.stdin)
phones = [device for devices in data["devices"].values() for device in devices
          if device.get("isAvailable") and device.get("name", "").startswith("iPhone")]
booted = next((device for device in phones if device.get("state") == "Booted"), None)
selected = booted or (phones[0] if phones else None)
if selected is None:
    raise SystemExit("No available iPhone simulator was found.")
print(selected["udid"])
'
}

assert_log() {
    local log="$1"
    grep -Fq 'Dynamic code supported: False' "$log" || fail "Dynamic code was reported as supported."
    grep -Fq 'Dynamic code compiled: False' "$log" || fail "Dynamic code was reported as compiled."
    grep -Fq 'LITEDB_IOS_AOT_RESULT=PASS' "$log" || fail "The iOS application did not report PASS."
}

run_mode() {
    local selected_mode="$1"
    local runtime_identifier
    local aot_mode

    case "$selected_mode" in
        simulator)
            runtime_identifier="iossimulator-arm64"
            aot_mode="simulator"
            ;;
        device-mono)
            runtime_identifier="ios-arm64"
            aot_mode="mono"
            ;;
        device-nativeaot)
            runtime_identifier="ios-arm64"
            aot_mode="nativeaot"
            ;;
        *) fail "Unknown mode '$selected_mode'. Use simulator, device-mono, device-nativeaot, or device-all." ;;
    esac

    local output_root="$repo_root/artifacts/ios-aot-validation/$selected_mode"
    local app="$repo_root/integration/LiteDB.iOSAotSmoke/bin/Release/net10.0-ios/$runtime_identifier/LiteDB.iOSAotSmoke.app"
    mkdir -p "$output_root"

    local common_properties=(
        "-p:IosAotMode=$aot_mode"
        "-p:TestingEnabled=false"
        "-p:ValidateXcodeVersion=${IOS_VALIDATE_XCODE_VERSION:-true}"
    )
    local signing_properties=()
    if [ "$runtime_identifier" = "ios-arm64" ]; then
        [ -n "${IOS_DEVICE_ID:-}" ] || fail 'IOS_DEVICE_ID is required for a physical-device run.'
        [ -n "${IOS_CODESIGN_KEY:-}" ] || fail 'IOS_CODESIGN_KEY is required for a physical-device run.'
        signing_properties=(
            "-p:CodesignKey=$IOS_CODESIGN_KEY"
            "-p:CodesignProvision=${IOS_CODESIGN_PROVISION:-Automatic}"
        )
    fi

    printf '[IOS-AOT] Restoring %s assets.\n' "$selected_mode"
    dotnet restore "$project" --runtime "$runtime_identifier" \
        "${common_properties[@]}" \
        ${signing_properties[@]+"${signing_properties[@]}"} > "$output_root/restore.log"

    printf '[IOS-AOT] Cleaning %s output so runtime payloads cannot leak between modes.\n' "$selected_mode"
    dotnet clean "$project" --configuration Release --runtime "$runtime_identifier" \
        "${common_properties[@]}" > "$output_root/clean.log"

    if [ "$selected_mode" = "simulator" ]; then
        printf '[IOS-AOT] Building %s.\n' "$selected_mode"
        dotnet build "$project" \
            --configuration Release \
            --runtime "$runtime_identifier" \
            "${common_properties[@]}" 2>&1 | tee "$output_root/build.log"
    else
        printf '[IOS-AOT] Publishing %s.\n' "$selected_mode"
        dotnet publish "$project" \
            --configuration Release \
            --runtime "$runtime_identifier" \
            "${common_properties[@]}" \
            ${signing_properties[@]+"${signing_properties[@]}"} 2>&1 | tee "$output_root/publish.log"
    fi

    [ -d "$app" ] || fail "Application bundle was not produced at $app."
    file "$app/LiteDB.iOSAotSmoke" | tee "$output_root/bundle.txt"

    local aot_data_count
    local managed_dll_count
    aot_data_count="$(find "$app" -maxdepth 1 -name '*.aotdata.arm64' | wc -l | tr -d ' ')"
    managed_dll_count="$(find "$app" -maxdepth 1 -name '*.dll' | wc -l | tr -d ' ')"
    printf 'AOT data files: %s\nManaged DLL files: %s\n' "$aot_data_count" "$managed_dll_count" | \
        tee -a "$output_root/bundle.txt"

    if [ "$selected_mode" = "device-nativeaot" ]; then
        [ "$aot_data_count" -eq 0 ] || fail 'NativeAOT bundle contains Mono AOT-data files.'
        [ "$managed_dll_count" -eq 0 ] || fail 'NativeAOT bundle contains managed DLLs.'
    else
        [ -f "$app/LiteDB.aotdata.arm64" ] || fail 'Mono AOT data for LiteDB is missing.'
        [ "$managed_dll_count" -gt 0 ] || fail 'Mono AOT bundle contains no managed DLLs.'
    fi

    if [ "$selected_mode" = "simulator" ]; then
        local simulator_id
        simulator_id="$(select_simulator)"
        printf '[IOS-AOT] Using simulator %s.\n' "$simulator_id"
        xcrun simctl boot "$simulator_id" 2> /dev/null || true
        xcrun simctl bootstatus "$simulator_id" -b
        xcrun simctl install "$simulator_id" "$app"
        xcrun simctl launch --console --terminate-running-process "$simulator_id" "$bundle_id" 2>&1 | \
            tee "$output_root/run.log"
    else
        xcrun devicectl device install app --device "$IOS_DEVICE_ID" "$app" | tee "$output_root/install.log"
        xcrun devicectl device process launch \
            --device "$IOS_DEVICE_ID" \
            --terminate-existing \
            --console \
            --timeout 60 \
            "$bundle_id" 2>&1 | tee "$output_root/run.log"
        grep -Fq 'The app terminated with the exit code 0.' "$output_root/run.log" || \
            fail 'The physical-device application did not exit with code 0.'
    fi

    assert_log "$output_root/run.log"
    printf '[IOS-AOT] Passed: %s.\n' "$selected_mode"
}

require_command dotnet
require_command xcrun
require_command python3

if [ "$mode" = "device-all" ]; then
    run_mode device-mono
    run_mode device-nativeaot
else
    run_mode "$mode"
fi
