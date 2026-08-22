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

printf '%s\n' '[PACKAGE-CONSUMER] Preparing a clean local package feed and NuGet cache.'
rm -rf "$FEED_DIRECTORY" "$PACKAGE_CACHE_DIRECTORY" "$GENERATED_DIRECTORY" "$PUBLISH_DIRECTORY" "$CONSUMER_DIRECTORY/bin" "$CONSUMER_DIRECTORY/obj"
mkdir -p "$FEED_DIRECTORY" "$PACKAGE_CACHE_DIRECTORY"

printf '%s\n' '[PACKAGE-CONSUMER] Packing the matching LiteDB runtime and source-generator package pair.'
dotnet pack "$ROOT_DIRECTORY/LiteDB/LiteDB.csproj" \
  --configuration Release \
  --nologo \
  -p:GitVersionEnabled=false \
  -p:PackageVersion="$PACKAGE_VERSION" \
  --output "$FEED_DIRECTORY"
dotnet pack "$ROOT_DIRECTORY/LiteDB.SourceGenerator/LiteDB.SourceGenerator.csproj" \
  --configuration Release \
  --nologo \
  -p:GitVersionEnabled=false \
  -p:PackageVersion="$PACKAGE_VERSION" \
  --output "$FEED_DIRECTORY"

test -f "$FEED_DIRECTORY/LiteDB.$PACKAGE_VERSION.nupkg"
test -f "$FEED_DIRECTORY/LiteDB.SourceGenerator.$PACKAGE_VERSION.nupkg"

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

printf '%s\n' '[PACKAGE-CONSUMER] Building the restored consumer and verifying generated mapper output.'
dotnet build "$CONSUMER_PROJECT" \
  --configuration Release \
  --no-restore \
  --nologo \
  -p:GitVersionEnabled=false
GENERATED_MAPPING_FILE="$(find "$GENERATED_DIRECTORY" -name LiteDbGeneratedMappings.g.cs -print -quit)"
test -n "$GENERATED_MAPPING_FILE"
grep -q 'PackagedGeneratedRecord' "$GENERATED_MAPPING_FILE"
grep -q 'SerializeDynamicDictionary' "$GENERATED_MAPPING_FILE"

printf '%s\n' '[PACKAGE-CONSUMER] Publishing and running the restored consumer as self-contained Native AOT.'
dotnet publish "$CONSUMER_PROJECT" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  --no-restore \
  --nologo \
  -p:GitVersionEnabled=false \
  /p:IlcParallelism=1 \
  --output "$PUBLISH_DIRECTORY"
"$PUBLISH_DIRECTORY/LiteDB.SourceGenerator.PackageConsumer"

printf '%s\n' '[PACKAGE-CONSUMER] Passed: local-feed package restore, analyzer discovery, generated mapping, and Native AOT execution.'
