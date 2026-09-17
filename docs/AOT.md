# Native AOT and source-generated entity mapping

LiteDB supports an opt-in typed-collection path for applications that publish with **Native AOT** and need to avoid runtime discovery of application model members. The path uses a C# incremental source generator to emit `EntityMapper` definitions at compile time. It replaces the former `LiteAotDatabase` wrapper and manual `EntityMapper` construction.

LiteDB itself targets `netstandard2.0`, `net8.0`, and `net10.0`; its AOT compatibility analysis is enabled for the .NET application targets (`net8.0` and `net10.0`). The consuming application must target **.NET 8 or later** when it publishes with Native AOT.

## Compatibility notes

`BsonMapper.ResolveCollectionName` changes from a public field to a property so its trimming contract can be caller-visible. This changes the binary member shape, so consumers must rebuild against the updated LiteDB package.

The newly annotated public virtual `BsonMapper.ToObject(Type, BsonDocument)` and `ToObject<T>(BsonDocument)` methods require external overrides to carry the same `RequiresUnreferencedCode` and `RequiresDynamicCode` attributes. Otherwise trim and AOT analysis reports override-contract warnings (`IL2046` and `IL3051`). The same rule applies when overriding other newly annotated mapper extension points.

## Configure the consuming project

### Published packages

External applications use a matching pair of packages: the `LiteDB` runtime package and the compile-time-only `LiteDB.SourceGenerator` analyzer package. Keep both package versions identical. The analyzer is intentionally an explicit opt-in and does not add a runtime assembly reference.

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <LiteDbPackageVersion>REPLACE_WITH_THE_MATCHING_RELEASE_VERSION</LiteDbPackageVersion>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="LiteDB" Version="$(LiteDbPackageVersion)" />
  <PackageReference Include="LiteDB.SourceGenerator"
                    Version="$(LiteDbPackageVersion)"
                    PrivateAssets="all" />
</ItemGroup>
```

`PrivateAssets="all"` keeps the analyzer private when this configuration is used in a reusable library. Applications may also use it; it does not prevent the current project from running the analyzer. Do not replace the analyzer reference with a runtime DLL reference or manually add the generated source file. NuGet discovers the analyzer from the package automatically.

### In-repository development

The repository test projects use a project reference while developing LiteDB itself. It is equivalent to the published analyzer package for local source builds, but is not the external-consumer configuration.

```xml
<ItemGroup>
  <ProjectReference Include="..\LiteDB\LiteDB.csproj" />
  <ProjectReference Include="..\LiteDB.SourceGenerator\LiteDB.SourceGenerator.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

For Native AOT, configure the executable project as follows. The runtime identifier must match the target platform you publish for.

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <ILLinkTreatWarningsAsErrors>true</ILLinkTreatWarningsAsErrors>
  <WarningsAsErrors>$(WarningsAsErrors);IL2026;IL2046;IL2057;IL2067;IL2070;IL2072;IL2075;IL3050;IL3051</WarningsAsErrors>
</PropertyGroup>
```

`ILLinkTreatWarningsAsErrors` only covers a trimmed, non-AOT publish. The Native AOT compiler has no equivalent switch and reports trim and AOT diagnostics as ordinary warnings, so list the codes in `WarningsAsErrors` (or enable `TreatWarningsAsErrors`) if a diagnostic should stop the publish.

Do not set `InvariantGlobalization` unless every data file the application opens uses the invariant collation. LiteDB stores a culture-specific collation in each data file and recreates that culture when the file is opened; invariant globalization mode cannot create cultures such as `en-US`.

Publish, for example, with:

```bash
dotnet publish -c Release -r linux-x64 --self-contained true
```

## Use generated mappings

Mark each supported entity with `[BsonSourceGenerated]`. During compilation, the generator emits `LiteDB.Generated.LiteDbGeneratedMappings`. Register all generated maps with a mapper, then request a collection by an explicit name.

```csharp
using System.Collections.Generic;
using LiteDB;
using LiteDB.Generated;

[BsonSourceGenerated]
public sealed class Customer
{
    public int Id { get; set; }

    [BsonField("name")]
    public string Name { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = [];

    [BsonIgnore]
    public string? TransientValue { get; set; }
}

var mapper = new BsonMapper();
LiteDbGeneratedMappings.Register(mapper);

using var database = new LiteDatabase("customers.db", mapper);
var customers = database.GetGeneratedCollection<Customer>("customers");
```

`Register` must run before `GetGeneratedCollection<T>`. Calling it twice for the same mapper throws `InvalidOperationException`. Generated registration is kept in a dedicated metadata registry and does not populate or replace the ordinary mapper cache, so `GetCollection<T>` continues to build and use ordinary reflection-capable metadata regardless of registration order. `GetGeneratedCollection<T>` throws when either the generated entity map or its generated execution map is absent. It never falls back to the ordinary runtime-mapped collection path.

The generator automatically registers a direct execution map for every supported model. Supported values include scalars, `List<string>`, rank-one `string[]`, and `Dictionary<string, object?>`; supported scalars include Boolean, the signed and unsigned integer widths, Single, Double, Decimal, Char, String, enum, `byte[]`, `DateTime`, `DateTimeOffset`, `Guid`, `ObjectId`, and nullable value-type forms of those values. `LiteDbGeneratedMappings.Register(mapper)` is the only registration call required; consumers do not call `RegisterGeneratedExecutionMap` manually. Direct maps perform generated document conversion and reproduce `SerializeNullValues`, `TrimWhitespace`, `EmptyStringToNull`, and `EnumAsInteger`; every other mapper-shaping setting is rejected before data access. Generated smoke fixtures guard the broad `ToDocument(Type, object)` and `ToObject(Type, BsonDocument)` methods so an accidental fallback is observable and build-breaking. Ordinary `GetCollection<T>` remains the reflection-capable LiteDB API. See [SourceGeneratorPackage.md](SourceGeneratorPackage.md) for package-version policy, contributor package checks, and release requirements.

## Supported model subset

The first source-generated mapping slice is intentionally narrow. The generator accepts a directly annotated, top-level, sealed, non-generic `public` or `internal` class or mutable record class with an accessible parameterless constructor. Sealing the annotated type prevents a generated collection from receiving a derived runtime type whose additional members are absent from the generated map. The generator can flatten supported public read/write instance properties declared by the annotated class and its public or internal, top-level, non-generic base classes. Compiler-generated record members, such as `EqualityContract`, are not persisted. A base class may be abstract; only the sealed concrete derived class is marked and directly constructed.

`[BsonId]`, `[BsonField]`, and `[BsonIgnore]` are supported on inherited and directly declared properties. For a virtual override chain, the generator maps only the most-derived property and resolves a mapping attribute from that declaration first, then from the nearest overridden declaration. The generator also applies LiteDB's `Id` and `<TypeName>Id` ID conventions. A `List<string>` is serialized and materialized through generated loops rather than reflection-based collection activation. An unannotated public getter-only property that is not an ID convention is treated as a computed projection and is excluded from persistence; mark it with `[BsonIgnore]` if explicit documentation is preferred. A getter-only `[BsonId]`, `[BsonField]`, or conventional ID remains unsupported because the generated path cannot hydrate it. Mapped `new`-hidden members, duplicate effective BSON field names, and multiple resolved IDs across an inheritance hierarchy produce `LDBSG003` rather than an ambiguous map.

| Supported | Not supported by the generated path |
|---|---|
| Scalar properties and nullable scalar value types, enums, `byte[]`, `DateTime`, `DateTimeOffset`, `DateTimeOffset?`, `Guid`, and `ObjectId` | Parameterized and `[BsonCtor]` constructors |
| `List<string>`, rank-one `string[]`, and `Dictionary<string, object?>` containing BSON-native dynamic values | Other array element types, other list element types, sets, typed or custom dictionaries, nested entities, and `BsonRef` |
| `[BsonId]`, `[BsonField]`, and `[BsonIgnore]` on direct and inherited properties; mutable record classes; unannotated computed getter-only projections; virtual override chains | Fields, persisted getter-only or init-only properties, mapped `new`-hidden members, duplicate BSON field names or IDs, generic or nested model/base classes |
| Explicit collection names | Default collection-name resolution and runtime mapper callbacks |

### DateTimeOffset representation

Ordinary and source-generated mapping write `DateTimeOffset` and nullable `DateTimeOffset` values as BSON `DateTime` values containing the UTC instant. BSON DateTime precision is one millisecond, so the original offset and any sub-millisecond ticks are not retained. Reads therefore materialize a UTC `DateTimeOffset`, consistently across ordinary managed, generated managed, and Native AOT execution paths.

Generated readers also accept the ticks-and-offset embedded document emitted by earlier source-generator versions. This compatibility is read-only: all new writes use the canonical BSON DateTime representation, which keeps indexes and queries independent of the typed writer used for a field.

### String array representation

Rank-one `string[]` properties use a BSON Array and generated indexed copy loops. With `BsonMapper.SerializeNullValues` enabled, a null string array is persisted as BSON Null and deserializes as null; an empty array remains a non-null, empty `string[]`. Other array element types remain unsupported.

### Nullable scalar values

A nullable scalar value type is supported when its underlying value type is supported. With the default mapper setting, null members retain LiteDB's normal omission behavior. When `BsonMapper.SerializeNullValues` is enabled, a nullable scalar `null` is persisted as BSON Null and deserializes as `null` through the generated mapping path.

### Dynamic dictionary representation

`Dictionary<string, object?>` is supported as a constrained BSON-native dynamic document. The generated path stores it as a BSON Document. It accepts null, `BsonValue`, BSON-native scalar values, and recursively nested dictionaries or arrays/enumerables. On materialization, nested BSON documents become `Dictionary<string, object?>` and nested BSON arrays become `object?[]`; other BSON values retain their raw CLR values.

With the default mapper setting, a null dictionary member follows LiteDB's normal omission behavior. When `BsonMapper.SerializeNullValues` is enabled, a null dictionary is persisted as BSON Null and deserializes as `null`. Arbitrary CLR objects, including `DateTimeOffset` values inside the dictionary, are intentionally rejected with `InvalidOperationException`. Typed dictionaries, dictionary interfaces, custom dictionary types, and arbitrary nested entity graphs remain unsupported by the generated path.

Unsupported annotated shapes produce an **error** diagnostic; the generated path never falls back to runtime member discovery. Use the existing runtime-mapped LiteDB APIs for models outside the supported generated subset.

### LINQ on generated collections

Lambda overloads (`Find`, `Count`, `Query().Where`, `OrderBy`, `Select`, `EnsureIndex`, `UpdateMany`, ...) are translated without runtime model mapping:

- A member is resolved only through a registered generated map. A member of any other type throws `InvalidOperationException` instead of being discovered through reflection.
- A captured value must be BSON-native (including enums, `DateTimeOffset`, `TimeSpan`, and `Uri`), a collection of such values, or an instance of a `[BsonSourceGenerated]` type. Reading a scalar member of a captured object (`x => x.Age > options.Minimum`) is fine; capturing the object itself as a value throws `NotSupportedException`.
- `Include` throws `NotSupportedException`, and a projection must produce a scalar or a `[BsonSourceGenerated]` type.

Two C# constructs make the *compiler* emit trim-unsafe `System.Linq.Expressions` calls into your own assembly, so the publish reports `IL2026` at your call site: object initializers (`x => new Customer { Name = x.Name }`, which is the only way to call the typed `UpdateMany`) and anonymous types (`x => new { x.Name }`). The object-initializer form works at runtime for a generated type because the generator keeps its accessors reachable; suppress the warning at that call site, or use the `BsonExpression` overloads. Anonymous-type projections are not supported on generated collections.

### What is not part of the Native AOT contract

`GetCollection<T>`, `LiteRepository`, `BsonMapper.Entity<T>()`, `BsonMapper.GetExpression`, `BsonMapper.ToDocument`/`ToObject`/`Serialize`/`Deserialize`, and file storage (`FileStorage`, `GetStorage<T>`) use runtime model mapping. They carry `RequiresUnreferencedCode` (and `RequiresDynamicCode` where types are constructed at runtime), so calling them from a trimmed or Native AOT application produces a diagnostic at the call site. `GetCollection(string)` (the `BsonDocument` API), SQL through `Execute`, and the engine features (transactions, encryption, shared mode, rebuild, pragmas, vector search) involve no model mapping and are validated published. A LINQ predicate on a `BsonDocument` collection may capture BSON-native values only.

| Diagnostic | Meaning | Typical remediation |
| --- | --- | --- |
| `LDBSG001` | Invalid source-generated model or inheritance hierarchy | Use a top-level, sealed, public/internal concrete class with an accessible parameterless constructor and supported base classes. |
| `LDBSG002` | Invalid source-generated property | Change, ignore, or remove an unsupported, inaccessible, indexed/static, or persisted getter-only property. |
| `LDBSG003` | Conflicting source-generated mapping | Remove duplicate mapped member names, IDs, conventional IDs, or effective BSON field names across the hierarchy. |

## Contributor validation

`LiteDB.AotTests` exercises generated registration, C2 direct scalar conversion with option-sensitive BSON golden documents and ordinary/direct cross-reads, mutable record classes, IDs, field and ignore attributes, `DateTimeOffset` values and cross-path reads, inherited and overridden properties, computed projections, `List<string>`, `string[]`, and dynamic-dictionary round trips. `LiteDB.AotSmokeTests` exercises an automatic C2 scalar execution checkpoint alongside generated scalar, nullable scalar, list, string-array, DateTimeOffset, inherited-property, computed-projection, and dynamic-dictionary workflows plus document, query, and stream scenarios.

The smoke project is also the feature-parity contract between publish modes. The parity script publishes it four times and runs every result:

| Mode | Purpose |
| --- | --- |
| `regular` | Untrimmed reference behavior. |
| `trimmed` | Trimmed managed single-file application. |
| `native-aot` | Native AOT, compiling only what the scenarios reach, as a real application would. |
| `native-aot-whole-library` | Native AOT with the whole LiteDB assembly rooted, so the compiler analyses every method of the library and not only the code a scenario happens to call. |

The gate fails when any publish log contains a trim or AOT diagnostic (`ILxxxx`), whatever its code, and when any transcript differs from the regular one. Scenarios write the values they observe into the transcript (`SmokeAssert.Report`), so the comparison covers results and not only the absence of exceptions. A new smoke scenario therefore expands all four gates together rather than relying on independently maintained test lists.

Besides generated mappings, the scenarios cover the engine through the document API: explicit transactions and durability across reopen, password encryption, checkpoint, pragmas and rebuild, a culture-specific collation, shared-mode connections, parallel writers, vector search, and LINQ over `BsonDocument`. Generated-collection scenarios run on a mapper whose runtime-mapping hooks all throw, so a silent fallback into reflection stops the run in every mode. CI runs the gate on Linux, Windows, and macOS for `net8.0` and `net10.0`.

Inside LiteDB itself, `IsAotCompatible` and the IL diagnostics-as-errors are enabled for the .NET targets, and the library contains no type-level trim or AOT suppressions: a member either carries `RequiresUnreferencedCode`/`RequiresDynamicCode`, or has a member-level suppression whose justification states the invariant that makes it safe.

Run the focused tests with:

```bash
dotnet test LiteDB.AotTests/LiteDB.AotTests.csproj -c Release
```

Run the Native AOT smoke test with:

```bash
dotnet publish LiteDB.AotSmokeTests/LiteDB.AotSmokeTests.csproj -c Release -r linux-x64 --self-contained true
./LiteDB.AotSmokeTests/bin/Release/net8.0/linux-x64/publish/LiteDB.AotSmokeTests
```

Run the complete regular-versus-published feature-parity gate with:

```bash
./scripts/validate-aot-feature-parity.sh
```

The script needs the Native AOT toolchain (`clang` and `zlib1g-dev` on Linux, the Xcode command-line tools on macOS, the MSVC build tools on Windows, where it runs from Git Bash). The runtime identifier defaults to the host; set `RUNTIME_IDENTIFIER` to override it and `TARGET_FRAMEWORK=net10.0` to validate the other runtime. Publish trees, publish logs, and transcripts are kept under `artifacts/aot-feature-parity/<framework>-<runtime>/`.

Run the separate trimmed, non-AOT gate with:

```bash
dotnet publish LiteDB.AotSmokeTests/LiteDB.AotSmokeTests.csproj -c Release -r linux-x64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=true -o artifacts/aot-smoke-trimmed
./artifacts/aot-smoke-trimmed/LiteDB.AotSmokeTests
```

Run the package-boundary Native AOT validation with:

```bash
./scripts/validate-source-generator-package-consumer.sh
```

The command creates a temporary local feed and NuGet cache, packs a matching LiteDB/runtime-analyzer pair, verifies the analyzer archive, restores an external package consumer, and publishes/runs that consumer as Native AOT. It needs the Linux Native AOT toolchain and uses NuGet.org only for SDK Native AOT and linker tooling; the LiteDB package pair itself is created in the local feed.
