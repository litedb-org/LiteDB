#!/usr/bin/env bash
set -euo pipefail

if (($# != 2)); then
    echo "Usage: $0 <package-directory> <package-version>" >&2
    exit 2
fi

readonly PACKAGE_DIRECTORY="$1"
readonly PACKAGE_VERSION="$2"
readonly RUNTIME_PACKAGE="$PACKAGE_DIRECTORY/LiteDB.$PACKAGE_VERSION.nupkg"
readonly ANALYZER_PACKAGE="$PACKAGE_DIRECTORY/LiteDB.SourceGenerator.$PACKAGE_VERSION.nupkg"

fail() {
    echo "[PACKAGE-ARCHIVE] $1" >&2
    exit 1
}

require_archive_entry() {
    local archive="$1"
    local entry="$2"
    unzip -Z1 "$archive" | grep -Fx "$entry" > /dev/null || fail "Missing '$entry' in $(basename "$archive")."
}

require_nuspec_value() {
    local nuspec="$1"
    local element="$2"
    local value="$3"
    grep -F "<$element>$value</$element>" "$nuspec" > /dev/null || fail "Expected <$element>$value</$element> in $(basename "$nuspec")."
}

test -d "$PACKAGE_DIRECTORY" || fail "Package directory does not exist: $PACKAGE_DIRECTORY"
test -f "$RUNTIME_PACKAGE" || fail "Missing runtime package: $RUNTIME_PACKAGE"
test -f "$ANALYZER_PACKAGE" || fail "Missing analyzer package: $ANALYZER_PACKAGE"

readonly RUNTIME_NUSPEC="$PACKAGE_DIRECTORY/runtime.nuspec"
readonly ANALYZER_NUSPEC="$PACKAGE_DIRECTORY/analyzer.nuspec"
unzip -p "$RUNTIME_PACKAGE" 'LiteDB.nuspec' > "$RUNTIME_NUSPEC"
unzip -p "$ANALYZER_PACKAGE" 'LiteDB.SourceGenerator.nuspec' > "$ANALYZER_NUSPEC"

require_nuspec_value "$RUNTIME_NUSPEC" id LiteDB
require_nuspec_value "$RUNTIME_NUSPEC" version "$PACKAGE_VERSION"
require_nuspec_value "$ANALYZER_NUSPEC" id LiteDB.SourceGenerator
require_nuspec_value "$ANALYZER_NUSPEC" version "$PACKAGE_VERSION"
require_nuspec_value "$ANALYZER_NUSPEC" developmentDependency true
grep -F '<license type="expression">MIT</license>' "$ANALYZER_NUSPEC" > /dev/null || fail "Expected MIT expression license in analyzer nuspec."

require_archive_entry "$ANALYZER_PACKAGE" 'README.md'
require_archive_entry "$ANALYZER_PACKAGE" 'icon_64x64.png'
require_archive_entry "$ANALYZER_PACKAGE" 'analyzers/dotnet/cs/LiteDB.SourceGenerator.dll'

analyzer_assets="$(unzip -Z1 "$ANALYZER_PACKAGE" | grep -E '^analyzers/dotnet/cs/.*\.dll$' || true)"
test "$analyzer_assets" = 'analyzers/dotnet/cs/LiteDB.SourceGenerator.dll' || fail "Analyzer package must contain exactly one standard C# analyzer DLL."

if unzip -Z1 "$ANALYZER_PACKAGE" | grep -E '^(lib|ref|runtimes|build|buildTransitive)/|^analyzers/dotnet/cs/net8\.0/|(^|/)LiteDB\.dll$|(^|/)Microsoft\.CodeAnalysis.*\.dll$|\.deps\.json$|\.runtimeconfig\.json$' > /dev/null; then
    fail "Analyzer package contains prohibited runtime, compiler, build, or framework-specific assets."
fi

if grep -F '<dependencies>' "$ANALYZER_NUSPEC" > /dev/null; then
    fail "Analyzer package nuspec must not contain a dependency group."
fi

rm -f "$RUNTIME_NUSPEC" "$ANALYZER_NUSPEC"
printf '[PACKAGE-ARCHIVE] Passed: LiteDB and LiteDB.SourceGenerator %s form a matching pair and the analyzer archive contains only the approved analyzer assets.\n' "$PACKAGE_VERSION"
