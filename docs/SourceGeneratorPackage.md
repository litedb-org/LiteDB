# LiteDB Source Generator package contract

## Purpose

`LiteDB.SourceGenerator` is the separately distributed **compile-time** companion for LiteDB source-generated mappings. It analyzes models marked with `[BsonSourceGenerated]` and emits `LiteDB.Generated.LiteDbGeneratedMappings` for registration with `BsonMapper`.

> The source generator is not a runtime replacement for LiteDB. It does not contain the LiteDB runtime implementation and must not become a runtime deployment dependency.

This document records the package contract approved before package asset layout and publication are enabled. It is primarily a contributor and release-owner contract. Consumer installation instructions are deferred until package contents and local-feed consumption are implemented and validated.

## Package identity and ownership

| Contract item | Decision |
| --- | --- |
| Package ID | `LiteDB.SourceGenerator` |
| Product | LiteDB Source Generator |
| License | MIT |
| Project URL | `https://www.litedb.org` |
| Repository | `https://github.com/litedb-org/LiteDB` |
| Package role | Development-time Roslyn analyzer only |
| Runtime package | `LiteDB` remains a separate runtime package |
| Ownership | The LiteDB release process owns both packages as one paired release |

The package uses the same repository provenance, license expression, icon, and GitVersion source as the LiteDB runtime package. It is marked as a development dependency. A consuming **library** should keep its analyzer reference private so the analyzer does not flow transitively to unrelated downstream projects.

## Compatibility contract

The future published analyzer asset will use the generator's `netstandard2.0` output under the standard `analyzers/dotnet/cs` package path. The repository retains the generator's `net8.0` target for build and analyzer validation, but that target is not the distributed analyzer asset.

The supported application path is a `net8.0` or later application that references a matching LiteDB runtime package, attaches the source generator at compile time, registers the generated mappings, and uses `GetGeneratedCollection<T>`. Native AOT support remains constrained by the generated mapping subset described in [AOT.md](AOT.md). The analyzer package must not introduce runtime Roslyn or LiteDB implementation assets into a published application.

Compiler-host compatibility is defined by the source generator's `IIncrementalGenerator` implementation and Roslyn API usage. A clean package-consumer build will establish the released compiler support baseline before the first package publication. The project must not claim broader compiler compatibility merely because an analyzer package is built for `netstandard2.0`.

## Versioning policy

`LiteDB` and `LiteDB.SourceGenerator` are an **exact version pair**. They are built from the same GitVersion-derived version and the same source revision. The source generator has no independent version prefix, version override, release train, or compatibility range.

A consumer must use matching package versions. A mismatched pair is unsupported even if it happens to compile, because generated source depends on LiteDB runtime mapping seams. The release process must not represent accidental mixed-version behavior as a supported compatibility guarantee.

| Situation | Support decision |
| --- | --- |
| Matching LiteDB and LiteDB.SourceGenerator versions from one release | Supported, subject to the documented generated-mapping subset |
| Analyzer version newer or older than LiteDB runtime version | Unsupported |
| Independent analyzer-only patch release | Not permitted under the current policy |
| Corrected generator behavior | Publish a corrected paired version of both packages |

## Consumer ownership model

The eventual consumer configuration requires two explicit references: the LiteDB runtime package and the matching source-generator analyzer package. The runtime package does not silently install the analyzer package, and the analyzer package does not bring LiteDB runtime implementation assets into an application.

For application projects, the analyzer is a direct compile-time dependency. For reusable libraries, the analyzer should be private to the library project unless the library intentionally wants its consumers to compile that library's annotated model source. Exact installation syntax is deferred to P1.4, after local-feed validation proves the package layout.

## Release and rollback policy

Release automation must pack, inspect, validate, and publish both packages from the same validated source revision. The package archive for the analyzer must be inspected before publication to verify that only intended analyzer/build metadata assets are present and that no LiteDB runtime DLL or Roslyn runtime dependency is shipped inadvertently.

A defective analyzer package must never be replaced in place under the same version. When a registry permits it, the affected package version may be unlisted; the remediation is always a newly published corrected **paired** version. The LiteDB runtime package must not be republished with different contents under an existing version.

## Implementation gates

| Gate | Required work before publication |
| --- | --- |
| P1.2 | Enable packing and explicitly place only the `netstandard2.0` generator DLL in `analyzers/dotnet/cs`; inspect the `.nupkg` contents. |
| P1.3 | Restore a clean external consumer from a local feed, build the matching package pair, register generated mappings, and publish/run a Native AOT consumer. |
| P1.4 | Add validated consumer installation commands, contributor package-validation commands, and release checklist entries. |

Until these gates pass, `LiteDB.SourceGenerator` remains non-packable. This prevents an incomplete package contract from being published before analyzer discovery, asset hygiene, and consumer behavior are proven.
