#!/usr/bin/env bash
set -euo pipefail

readonly ROOT_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
readonly PACKAGE_VERSION="0.0.0-sourcegenerator-p13.1"
readonly FEED_DIRECTORY="$ROOT_DIRECTORY/artifacts/source-generator-package-feed"
readonly PACKAGE_CACHE_DIRECTORY="$ROOT_DIRECTORY/artifacts/source-generator-package-cache"
readonly CONSUMER_PROJECT="$ROOT_DIRECTORY/LiteDB.SourceGenerator.PackageConsumer/LiteDB.SourceGenerator.PackageConsumer.csproj"
readonly CONSUMER_DIRECTORY="$ROOT_DIRECTORY/LiteDB.SourceGenerator.PackageConsumer"
readonly GENERATED_DIRECTORY="$CONSUMER_DIRECTORY/obj/generated"
readonly PUBLISH_DIRECTORY="$ROOT_DIRECTORY/artifacts/source-generator-package-consumer-native-aot"
readonly TRIMMED_DIRECTORY="$ROOT_DIRECTORY/artifacts/source-generator-package-consumer-trimmed"

# Fails on any trim or AOT diagnostic in a publish log, whatever its code: the ILCompiler targets have no
# warnings-as-errors switch, and the consumer csproj can only promote the codes it lists. Under pipefail the
# pipeline fails when grep matches nothing, so the branch runs only when diagnostics exist.
fail_on_il_diagnostics() {
  if grep -E '(warning|error) IL[0-9]{4}' "$1" | sort -u; then
    printf '%s\n' "[PACKAGE-CONSUMER] FAILED: $1 contains trim or AOT diagnostics." >&2
    exit 1
  fi
}

printf '%s\n' '[PACKAGE-CONSUMER] Preparing a clean local package feed and NuGet cache.'
rm -rf "$FEED_DIRECTORY" "$PACKAGE_CACHE_DIRECTORY" "$GENERATED_DIRECTORY" "$PUBLISH_DIRECTORY" "$TRIMMED_DIRECTORY" "$CONSUMER_DIRECTORY/bin" "$CONSUMER_DIRECTORY/obj"
mkdir -p "$FEED_DIRECTORY" "$PACKAGE_CACHE_DIRECTORY"

printf '%s\n' '[PACKAGE-CONSUMER] Packing the matching LiteDB runtime and source-generator package pair.'
dotnet pack "$ROOT_DIRECTORY/LiteDB/LiteDB.csproj" \
  --configuration PackageValidation \
  --nologo \
  -p:TestingEnabled=false \
  -p:Optimize=true \
  -p:GitVersionEnabled=false \
  -p:PackageVersion="$PACKAGE_VERSION" \
  --output "$FEED_DIRECTORY"
dotnet pack "$ROOT_DIRECTORY/LiteDB.SourceGenerator/LiteDB.SourceGenerator.csproj" \
  --configuration PackageValidation \
  --nologo \
  -p:Optimize=true \
  -p:GitVersionEnabled=false \
  -p:PackageVersion="$PACKAGE_VERSION" \
  --output "$FEED_DIRECTORY"

test -f "$FEED_DIRECTORY/LiteDB.$PACKAGE_VERSION.nupkg"
test -f "$FEED_DIRECTORY/LiteDB.SourceGenerator.$PACKAGE_VERSION.nupkg"
"$ROOT_DIRECTORY/scripts/validate-source-generator-package-archive.sh" "$FEED_DIRECTORY" "$PACKAGE_VERSION"

printf '%s\n' '[PACKAGE-CONSUMER] Restoring the external consumer with the local package feed and required Native AOT toolchain source.'
export NUGET_PACKAGES="$PACKAGE_CACHE_DIRECTORY"
dotnet restore "$CONSUMER_PROJECT" \
  --configfile "$CONSUMER_DIRECTORY/NuGet.config" \
  --no-cache \
  --force-evaluate \
  --runtime linux-x64 \
  --nologo \
  -p:GitVersionEnabled=false

grep -q "\"LiteDB/$PACKAGE_VERSION\"" "$CONSUMER_DIRECTORY/obj/project.assets.json"
grep -q "\"LiteDB.SourceGenerator/$PACKAGE_VERSION\"" "$CONSUMER_DIRECTORY/obj/project.assets.json"
grep -q 'analyzers/dotnet/cs/LiteDB.SourceGenerator.dll' "$CONSUMER_DIRECTORY/obj/project.assets.json"

printf '%s\n' '[PACKAGE-CONSUMER] Requiring warnings for custom file IDs and persisted type lookup.'
warning_probe_log="$FEED_DIRECTORY/runtime-mapping-warning-probe.log"
if dotnet build "$CONSUMER_PROJECT" --configuration Release --no-restore --nologo \
  -p:GitVersionEnabled=false -p:DefineConstants=RUNTIME_MAPPING_WARNING_PROBE > "$warning_probe_log" 2>&1; then
  printf '%s\n' '[PACKAGE-CONSUMER] FAILED: unsafe runtime mapping compiled without diagnostics.' >&2
  exit 1
fi
for api in 'LiteDatabase.GetStorage' 'ILiteDatabase.GetStorage' 'LiteStorage<TFileId>.LiteStorage'; do
  for diagnostic in IL2026 IL3050; do
    grep -F "error $diagnostic:" "$warning_probe_log" | grep -F "$api" > /dev/null || {
      cat "$warning_probe_log" >&2
      printf '[PACKAGE-CONSUMER] FAILED: missing %s on %s.\n' "$diagnostic" "$api" >&2
      exit 1
    }
  done
done
for api in 'DefaultTypeNameBinder.GetType' 'ITypeNameBinder.GetType'; do
  grep -F 'error IL2026:' "$warning_probe_log" | grep -F "$api" > /dev/null || {
    cat "$warning_probe_log" >&2
    printf '[PACKAGE-CONSUMER] FAILED: missing IL2026 on %s.\n' "$api" >&2
    exit 1
  }
done

printf '%s\n' '[PACKAGE-CONSUMER] Building the restored consumer and verifying generated mapper output.'
dotnet build "$CONSUMER_PROJECT" \
  --configuration Release \
  --no-restore \
  --nologo \
  -p:GitVersionEnabled=false
GENERATED_MAPPING_FILE="$(find "$GENERATED_DIRECTORY" -name 'LiteDbGeneratedMappings*.g.cs' -print -quit)"
test -n "$GENERATED_MAPPING_FILE"
grep -q 'PackagedGeneratedRecord' "$GENERATED_MAPPING_FILE"
grep -q 'RegisterGeneratedExecutionMap' "$GENERATED_MAPPING_FILE"

printf '%s\n' '[PACKAGE-CONSUMER] Publishing and running the restored consumer as a trimmed, non-AOT executable.'
dotnet publish "$CONSUMER_PROJECT" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  --no-restore \
  --nologo \
  -p:GitVersionEnabled=false \
  -p:PublishAot=false \
  -p:PublishTrimmed=true \
  --output "$TRIMMED_DIRECTORY" 2>&1 | tee "$TRIMMED_DIRECTORY.publish.log"
fail_on_il_diagnostics "$TRIMMED_DIRECTORY.publish.log"
"$TRIMMED_DIRECTORY/LiteDB.SourceGenerator.PackageConsumer"

printf '%s\n' '[PACKAGE-CONSUMER] Publishing and running the restored consumer as self-contained Native AOT.'
dotnet publish "$CONSUMER_PROJECT" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  --no-restore \
  --nologo \
  -p:GitVersionEnabled=false \
  /p:IlcParallelism=1 \
  --output "$PUBLISH_DIRECTORY" 2>&1 | tee "$PUBLISH_DIRECTORY.publish.log"
fail_on_il_diagnostics "$PUBLISH_DIRECTORY.publish.log"
"$PUBLISH_DIRECTORY/LiteDB.SourceGenerator.PackageConsumer"

printf '%s\n' '[PACKAGE-CONSUMER] Passed: local-feed package restore, analyzer discovery, generated mapping, and Native AOT execution.'
