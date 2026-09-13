# LiteDB.SourceGenerator package contract

`LiteDB.SourceGenerator` is a **development-time Roslyn analyzer** that emits LiteDB entity-mapper registration code for explicitly annotated models. It is distributed separately from the LiteDB runtime package. The analyzer package contains no LiteDB runtime implementation, no Roslyn compiler assemblies, and no runtime deployment asset.

## Version and ownership contract

`LiteDB` and `LiteDB.SourceGenerator` are a matching release pair. Both use the version calculated from the same source revision by the repository’s GitVersion configuration. Only exact matching versions are supported; a mixed pair is unsupported even if it happens to compile.

The analyzer package is a development dependency with the same MIT license, project URL, icon, and repository provenance as LiteDB. It packages exactly one analyzer assembly, `analyzers/dotnet/cs/LiteDB.SourceGenerator.dll`, built for `netstandard2.0`. The repository retains the generator’s `net8.0` target for local build and analyzer validation, but it is not a distributed analyzer asset.

> The analyzer package is compile-time only. It does not add LiteDB or Roslyn implementation assemblies to a published application.

## Consumer configuration

A Native AOT application targets `net8.0` or later and references the matching package pair. The source-generator reference is private so reusable libraries do not flow an analyzer reference transitively.

```xml
<PropertyGroup>
  <LiteDbPackageVersion>YOUR_MATCHING_LITEDB_VERSION</LiteDbPackageVersion>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="LiteDB" Version="$(LiteDbPackageVersion)" />
  <PackageReference Include="LiteDB.SourceGenerator"
                    Version="$(LiteDbPackageVersion)"
                    PrivateAssets="all" />
</ItemGroup>
```

Annotate supported model classes with `[BsonSourceGenerated]`, then register the generated mappings once for each `BsonMapper` before calling `GetGeneratedCollection<T>`:

```csharp
using LiteDB;
using LiteDB.Generated;

var mapper = new BsonMapper();
LiteDbGeneratedMappings.Register(mapper);

using var database = new LiteDatabase("app.db", mapper: mapper);
var records = database.GetGeneratedCollection<GeneratedRecord>("records");
```

The generated mapping subset, Native AOT application configuration, diagnostics, and dynamic-dictionary contract are documented in the [Native AOT and Source-Generated Entity Mapping guide](https://github.com/litedb-org/LiteDB/blob/master/docs/AOT.md).

For repository development, continue to use the analyzer-only project reference documented in that guide. External consumers should use the package references above; they must not attach analyzer DLLs manually or use local project references to simulate package discovery.

## Contributor validation

The following command builds matching local packages, verifies their archive contract, restores a package-only consumer from a clean local feed, confirms generated source discovery and automatic execution-map registration, and publishes/runs the same consumer first as a trimmed non-AOT executable and then with self-contained Linux x64 Native AOT:

```bash
./scripts/validate-source-generator-package-consumer.sh
```

The validator uses NuGet.org only for standard Native AOT/ILLink toolchain packages. The exact LiteDB runtime and source-generator versions are created in, and restored from, the script’s local feed. It verifies those exact package IDs and the analyzer asset path in the restored assets file.

To inspect already-created paired archives without building or executing them, run:

```bash
./scripts/validate-source-generator-package-archive.sh artifacts YOUR_MATCHING_LITEDB_VERSION
```

The archive validator requires both `LiteDB.<version>.nupkg` and `LiteDB.SourceGenerator.<version>.nupkg`. It verifies matching IDs/versions, development dependency metadata, icon/readme, the sole C# analyzer asset, and absence of runtime, compiler, dependency, build, `net8.0` analyzer, or library assets.

## Release and rollback policy

Prerelease publishing inherits the package-consumer and source-project Native AOT gates from the reusable Linux CI workflow. The manual stable-release workflow runs those gates directly before it packs both packages and validates the final archive pair. Both workflows create matching archives in `artifacts`, validate them with `validate-source-generator-package-archive.sh`, and only then reach their existing NuGet push or GitHub release-artifact step.

A defective analyzer package version must be corrected through a new matching LiteDB/runtime and analyzer pair. Do not replace or republish archive contents under an existing version. When a package must be withdrawn, unlist it where the registry permits and publish a corrected paired version.

| Release gate | Required evidence |
| --- | --- |
| Exact pair | One GitVersion-derived version names both LiteDB and LiteDB.SourceGenerator archives. |
| Archive hygiene | `validate-source-generator-package-archive.sh artifacts <version>` passes after both pack operations. |
| Package consumer | The Linux Native AOT CI job passed `validate-source-generator-package-consumer.sh`; the manual stable-release workflow runs it directly. |
| Generated execution | Every admitted property family and inheritance/attribute path has automatic execution-map registration; guarded smoke mappers fail if broad generic conversion is reached. |
| Source regressions | Focused managed generated-mapping tests, compiler snapshots, trimmed publish, and source-project Native AOT smoke tests passed in CI; the manual stable-release workflow runs these gates directly. |
| Security/release advisories | The runtime package’s current NuGet audit advisory and package-readme advisory have been remediated or explicitly accepted by the runtime package security/release owner. They are not suppressed or attributed to the analyzer package. |
| Publication immutability | Neither package archive is replaced under an existing version; corrections use a new paired version. |
