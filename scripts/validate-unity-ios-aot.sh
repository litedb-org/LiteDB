#!/usr/bin/env bash
#
# Exports the checked-in Unity fixture through IL2CPP, builds it with Xcode, installs it, and requires its
# runtime PASS marker. Default and forced modes prove both LiteDB expression-delegate paths.
#
# Usage:
#   ./scripts/validate-unity-ios-aot.sh simulator-default
#   ./scripts/validate-unity-ios-aot.sh simulator-forced
#   ./scripts/validate-unity-ios-aot.sh simulator-all
#   IOS_DEVICE_ID=<udid> IOS_DEVELOPMENT_TEAM=<team-id> \
#     ./scripts/validate-unity-ios-aot.sh device-forced
#
# Optional environment:
#   UNITY_PATH          Unity executable; defaults to the version pinned by the fixture
#   IOS_SIMULATOR_ID    simulator UDID; a booted/available iPhone is selected otherwise
#   DEVELOPER_DIR       selects a non-default Xcode installation
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/integration/LiteDB.UnityIosAotSmoke"
mode="${1:-simulator-all}"
bundle_id="org.litedb.unityiosaotsmoke"
product_name="LiteDBUnityiOSAOTSmoke"
unity="${UNITY_PATH:-/Applications/Unity/Hub/Editor/6000.0.84f1/Unity.app/Contents/MacOS/Unity}"

fail() {
    printf '[UNITY-IOS-AOT] FAILED: %s\n' "$1" >&2
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

copy_unity_dependencies() {
    printf '[UNITY-IOS-AOT] Building the netstandard2.0 LiteDB assembly.\n'
    dotnet build "$repo_root/LiteDB/LiteDB.csproj" \
        --configuration Release \
        --framework netstandard2.0 \
        -p:TestingEnabled=false

    local deps="$repo_root/LiteDB/bin/Release/netstandard2.0/LiteDB.deps.json"
    local unsafe_version
    local package_root
    local unsafe_dll
    unsafe_version="$(python3 -c '
import json, sys
data = json.load(open(sys.argv[1]))
print(next(key.split("/", 1)[1] for key in data["libraries"]
           if key.startswith("System.Runtime.CompilerServices.Unsafe/")))
' "$deps")"
    package_root="$(dotnet nuget locals global-packages --list | sed 's/^[^:]*: //')"
    unsafe_dll="$package_root/system.runtime.compilerservices.unsafe/$unsafe_version/lib/netstandard2.0/System.Runtime.CompilerServices.Unsafe.dll"
    [ -f "$unsafe_dll" ] || fail "Could not locate System.Runtime.CompilerServices.Unsafe $unsafe_version."

    mkdir -p "$project/Assets/Plugins"
    cp "$repo_root/LiteDB/bin/Release/netstandard2.0/LiteDB.dll" "$project/Assets/Plugins/LiteDB.dll"
    cp "$unsafe_dll" "$project/Assets/Plugins/System.Runtime.CompilerServices.Unsafe.dll"
}

run_mode() {
    local selected_mode="$1"
    local target
    local forced

    case "$selected_mode" in
        simulator-default) target="simulator"; forced="false" ;;
        simulator-forced) target="simulator"; forced="true" ;;
        device-default) target="device"; forced="false" ;;
        device-forced) target="device"; forced="true" ;;
        *) fail "Unknown mode '$selected_mode'." ;;
    esac

    if [ "$target" = "device" ]; then
        [ -n "${IOS_DEVICE_ID:-}" ] || fail 'IOS_DEVICE_ID is required for a physical-device run.'
        [ -n "${IOS_DEVELOPMENT_TEAM:-}" ] || fail 'IOS_DEVELOPMENT_TEAM is required for a physical-device run.'
    fi

    local output_root="$repo_root/artifacts/unity-ios-aot-validation/$selected_mode"
    case "$output_root" in
        "$repo_root"/artifacts/unity-ios-aot-validation/*) ;;
        *) fail "Refusing to clean unexpected output path '$output_root'." ;;
    esac
    rm -rf -- "$output_root"
    mkdir -p "$output_root"

    printf '[UNITY-IOS-AOT] Exporting %s through Unity IL2CPP.\n' "$selected_mode"
    LITEDB_UNITY_TARGET="$target" \
    LITEDB_UNITY_FORCE_SINGLE_ARGUMENT_DELEGATES="$forced" \
    LITEDB_UNITY_BUILD_PATH="$output_root/xcode" \
        "$unity" \
        -batchmode \
        -nographics \
        -quit \
        -accept-apiupdate \
        -projectPath "$project" \
        -executeMethod UnityIosBuild.Build \
        -logFile - 2>&1 | tee "$output_root/unity.log"
    grep -Fq 'LITEDB_UNITY_IOS_BUILD=PASS' "$output_root/unity.log" || fail 'Unity did not report a successful export.'

    local derived_data="$output_root/DerivedData"
    local app
    if [ "$target" = "simulator" ]; then
        local simulator_id
        simulator_id="$(select_simulator)"
        printf '[UNITY-IOS-AOT] Building for simulator %s.\n' "$simulator_id"
        xcrun simctl boot "$simulator_id" 2> /dev/null || true
        xcrun simctl bootstatus "$simulator_id" -b
        xcodebuild \
            -project "$output_root/xcode/Unity-iPhone.xcodeproj" \
            -scheme Unity-iPhone \
            -configuration Release \
            -sdk iphonesimulator \
            -destination "platform=iOS Simulator,id=$simulator_id" \
            -derivedDataPath "$derived_data" \
            CODE_SIGNING_ALLOWED=NO \
            build 2>&1 | tee "$output_root/xcodebuild.log"
        app="$derived_data/Build/Products/Release-iphonesimulator/$product_name.app"
        [ -d "$app" ] || fail "Simulator application bundle was not produced at $app."
        xcrun simctl install "$simulator_id" "$app"
        xcrun simctl launch --console --terminate-running-process "$simulator_id" "$bundle_id" \
            > "$output_root/run.log" 2>&1 &
        local launch_pid="$!"
        local reported_result=false
        for _ in {1..60}; do
            if grep -Fq 'LITEDB_UNITY_IOS_IL2CPP_RESULT=' "$output_root/run.log"; then
                reported_result=true
                break
            fi
            kill -0 "$launch_pid" 2> /dev/null || break
            sleep 1
        done
        xcrun simctl terminate "$simulator_id" "$bundle_id" 2> /dev/null || true
        if kill -0 "$launch_pid" 2> /dev/null; then
            kill "$launch_pid" 2> /dev/null || true
        fi
        wait "$launch_pid" 2> /dev/null || true
        cat "$output_root/run.log"
        [ "$reported_result" = true ] || fail 'The Unity simulator player did not report a result within 60 seconds.'
    else
        printf '[UNITY-IOS-AOT] Building and signing for physical iPhoneOS.\n'
        xcodebuild \
            -project "$output_root/xcode/Unity-iPhone.xcodeproj" \
            -scheme Unity-iPhone \
            -configuration Release \
            -sdk iphoneos \
            -destination 'generic/platform=iOS' \
            -derivedDataPath "$derived_data" \
            -allowProvisioningUpdates \
            "DEVELOPMENT_TEAM=$IOS_DEVELOPMENT_TEAM" \
            CODE_SIGN_STYLE=Automatic \
            build 2>&1 | tee "$output_root/xcodebuild.log"
        app="$derived_data/Build/Products/Release-iphoneos/$product_name.app"
        [ -d "$app" ] || fail "Device application bundle was not produced at $app."
        xcrun devicectl device install app --device "$IOS_DEVICE_ID" "$app" | tee "$output_root/install.log"

        set +e
        xcrun devicectl device process launch \
            --device "$IOS_DEVICE_ID" \
            --terminate-existing \
            --console \
            --timeout 30 \
            "$bundle_id" 2>&1 | tee "$output_root/run.log"
        local launch_status="${PIPESTATUS[0]}"
        set -e

        local result_file="$output_root/unity-result.txt"
        xcrun devicectl device copy from \
            --device "$IOS_DEVICE_ID" \
            --domain-type appDataContainer \
            --domain-identifier "$bundle_id" \
            --source Documents/unity-result.txt \
            --destination "$result_file" | tee "$output_root/copy-result.log"
        grep -Fq 'LITEDB_UNITY_IOS_IL2CPP_RESULT=PASS' "$result_file" || \
            fail 'The copied physical-device result did not report PASS.'
        if [ "$launch_status" -ne 0 ]; then
            printf '[UNITY-IOS-AOT] devicectl exited %s after the player wrote PASS; Unity players may remain alive after Environment.Exit.\n' "$launch_status"
        fi
    fi

    grep -Fq "Force single-argument expression delegates: $([ "$forced" = true ] && printf True || printf False)" \
        "$output_root/run.log" || fail 'The player did not report the requested delegate mode.'
    grep -Fq 'LITEDB_UNITY_IOS_IL2CPP_RESULT=PASS' "$output_root/run.log" || \
        fail 'The Unity player console did not report PASS.'
    printf '[UNITY-IOS-AOT] Passed: %s.\n' "$selected_mode"
}

require_command dotnet
require_command python3
require_command xcodebuild
require_command xcrun
[ -x "$unity" ] || fail "Unity was not found at '$unity'. Set UNITY_PATH to its executable."
if [ "$(uname -m)" = "arm64" ] && ! /usr/bin/arch -x86_64 /usr/bin/true 2> /dev/null; then
    fail "Unity's iOS IL2CPP toolchain requires Rosetta 2. Install it with 'softwareupdate --install-rosetta'."
fi

copy_unity_dependencies

case "$mode" in
    simulator-all)
        run_mode simulator-default
        run_mode simulator-forced
        ;;
    device-all)
        run_mode device-default
        run_mode device-forced
        ;;
    *) run_mode "$mode" ;;
esac
