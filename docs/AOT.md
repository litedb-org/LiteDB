# Native AOT and source-generated entity mapping

LiteDB supports an opt-in typed-collection path for applications that publish with **Native AOT** and need to avoid runtime discovery of application model members. The path uses a C# incremental source generator to emit `EntityMapper` definitions at compile time. It replaces the former `LiteAotDatabase` wrapper and manual `EntityMapper` construction.

LiteDB itself targets `netstandard2.0` and `net8.0`; its AOT compatibility analysis is enabled only for the `net8.0` target. The consuming application must target **.NET 8 or later** when it publishes with Native AOT.

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
  <InvariantGlobalization>true</InvariantGlobalization>
  <ILLinkTreatWarningsAsErrors>true</ILLinkTreatWarningsAsErrors>
</PropertyGroup>
```

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

`Register` must run before `GetGeneratedCollection<T>`. Calling it twice for the same mapper throws `InvalidOperationException`. `GetGeneratedCollection<T>` also throws when no generated entity map for `T` has been registered. This is intentional: it prevents the generated entry point from silently constructing an entity map at runtime.

Phase C C2.1 automatically registers a direct execution map when **every persisted member** is an admitted scalar: Boolean, the signed and unsigned integer widths, Single, Double, Decimal, Char, String, enum, `byte[]`, `DateTime`, `Guid`, `ObjectId`, or a nullable value-type form of one of those values. `LiteDbGeneratedMappings.Register(mapper)` is the only registration call required. Such collections execute `Insert`, `Update`, `FindById`, `Count`, and `Delete` through generated document conversion; no consumer calls `RegisterGeneratedExecutionMap` manually. The direct path reproduces `SerializeNullValues`, `TrimWhitespace`, `EmptyStringToNull`, and `EnumAsInteger`; it rejects other mapper-shaping settings before data access. `DateTimeOffset`, lists, string arrays, dynamic dictionaries, inheritance/attribute shapes, and other valid generated models currently retain the temporary generated-entity-map bridge until their independent Phase C compatibility rows are complete. Batch, explicit-ID, and upsert direct operations are also deferred. Ordinary `GetCollection<T>` remains the reflection-capable LiteDB API. See [SourceGeneratorPackage.md](SourceGeneratorPackage.md) for package-version policy, contributor package checks, and release requirements.

## Supported model subset

The first source-generated mapping slice is intentionally narrow. The generator accepts a directly annotated, top-level, non-abstract, non-generic `public` or `internal` class or mutable record class with an accessible parameterless constructor. It can flatten supported public read/write instance properties declared by that class and its public or internal, top-level, non-generic base classes. Compiler-generated record members, such as `EqualityContract`, are not persisted. A base class may be abstract; only the concrete derived class is marked and directly constructed.

`[BsonId]`, `[BsonField]`, and `[BsonIgnore]` are supported on inherited and directly declared properties. For a virtual override chain, the generator maps only the most-derived property and resolves a mapping attribute from that declaration first, then from the nearest overridden declaration. The generator also applies LiteDB's `Id` and `<TypeName>Id` ID conventions. A `List<string>` is serialized and materialized through generated loops rather than reflection-based collection activation. An unannotated public getter-only property that is not an ID convention is treated as a computed projection and is excluded from persistence; mark it with `[BsonIgnore]` if explicit documentation is preferred. A getter-only `[BsonId]`, `[BsonField]`, or conventional ID remains unsupported because the generated path cannot hydrate it. Mapped `new`-hidden members, duplicate effective BSON field names, and multiple resolved IDs across an inheritance hierarchy produce `LDBSG003` rather than an ambiguous map.

| Supported | Not supported by the generated path |
|---|---|
| Scalar properties and nullable scalar value types, enums, `byte[]`, `DateTime`, `DateTimeOffset`, `DateTimeOffset?`, `Guid`, and `ObjectId` | Parameterized and `[BsonCtor]` constructors |
| `List<string>`, rank-one `string[]`, and `Dictionary<string, object?>` containing BSON-native dynamic values | Other array element types, other list element types, sets, typed or custom dictionaries, nested entities, and `BsonRef` |
| `[BsonId]`, `[BsonField]`, and `[BsonIgnore]` on direct and inherited properties; mutable record classes; unannotated computed getter-only projections; virtual override chains | Fields, persisted getter-only or init-only properties, mapped `new`-hidden members, duplicate BSON field names or IDs, generic or nested model/base classes |
| Explicit collection names | Default collection-name resolution and runtime mapper callbacks |

### DateTimeOffset representation

Generated writes preserve `DateTimeOffset` and nullable `DateTimeOffset` values as an embedded BSON document with two `Int64` fields: `DateTime` contains `DateTimeOffset.Ticks` and `Offset` contains `DateTimeOffset.Offset.Ticks`. This retains the original offset and 100-nanosecond ticks.

Ordinary `BsonMapper` writes retain LiteDB's existing BSON `DateTime` representation, using the UTC instant. That representation loses the original offset and is limited to its BSON DateTime precision. Both mapping paths can read either representation: a generated reader interprets an ordinary BSON DateTime as a UTC `DateTimeOffset`, while the ordinary mapper reads the generated ticks-and-offset document exactly. The writer representation therefore remains observable. A field written through both paths can contain both BSON types, so applications must not assume that an existing date index or range query has representation-independent behavior. Use a deliberate collection migration or a representation-specific BSON expression when query/index semantics matter.

### String array representation

Rank-one `string[]` properties use a BSON Array and generated indexed copy loops. With `BsonMapper.SerializeNullValues` enabled, a null string array is persisted as BSON Null and deserializes as null; an empty array remains a non-null, empty `string[]`. Other array element types remain unsupported.

### Nullable scalar values

A nullable scalar value type is supported when its underlying value type is supported. With the default mapper setting, null members retain LiteDB's normal omission behavior. When `BsonMapper.SerializeNullValues` is enabled, a nullable scalar `null` is persisted as BSON Null and deserializes as `null` through the generated mapping path.

### Dynamic dictionary representation

`Dictionary<string, object?>` is supported as a constrained BSON-native dynamic document. The generated path stores it as a BSON Document. It accepts null, `BsonValue`, BSON-native scalar values, and recursively nested dictionaries or arrays/enumerables. On materialization, nested BSON documents become `Dictionary<string, object?>` and nested BSON arrays become `object?[]`; other BSON values retain their raw CLR values.

With the default mapper setting, a null dictionary member follows LiteDB's normal omission behavior. When `BsonMapper.SerializeNullValues` is enabled, a null dictionary is persisted as BSON Null and deserializes as `null`. Arbitrary CLR objects, including `DateTimeOffset` values inside the dictionary, are intentionally rejected with `InvalidOperationException`. Typed dictionaries, dictionary interfaces, custom dictionary types, and arbitrary nested entity graphs remain unsupported by the generated path.

Unsupported annotated shapes produce a fail-closed **error** diagnostic; the generated path never falls back to runtime member discovery. Use the existing runtime-mapped LiteDB APIs for models outside the supported generated subset.

| Diagnostic | Meaning | Typical remediation |
| --- | --- | --- |
| `LDBSG001` | Invalid source-generated model or inheritance hierarchy | Use a top-level public/internal concrete class with an accessible parameterless constructor and supported base classes. |
| `LDBSG002` | Invalid source-generated property | Change, ignore, or remove an unsupported, inaccessible, indexed/static, or persisted getter-only property. |
| `LDBSG003` | Conflicting source-generated mapping | Remove duplicate mapped member names, IDs, conventional IDs, or effective BSON field names across the hierarchy. |

## Contributor validation

`LiteDB.AotTests` exercises generated registration, C2 direct scalar conversion with option-sensitive BSON golden documents and ordinary/direct cross-reads, mutable record classes, IDs, field and ignore attributes, `DateTimeOffset` values and cross-path reads, inherited and overridden properties, computed projections, `List<string>`, `string[]`, and dynamic-dictionary round trips. `LiteDB.AotSmokeTests` publishes and runs a Native AOT executable with an automatic C2 scalar execution checkpoint alongside generated scalar, nullable scalar, list, string-array, DateTimeOffset, inherited-property, computed-projection, and dynamic-dictionary workflows plus document, query, and stream scenarios.

Run the focused tests with:

```bash
dotnet test LiteDB.AotTests/LiteDB.AotTests.csproj -c Release
```

Run the Native AOT smoke test with:

```bash
dotnet publish LiteDB.AotSmokeTests/LiteDB.AotSmokeTests.csproj -c Release -r linux-x64 --self-contained true
./LiteDB.AotSmokeTests/bin/Release/net8.0/linux-x64/publish/LiteDB.AotSmokeTests
```

Run the package-boundary Native AOT validation with:

```bash
./scripts/validate-source-generator-package-consumer.sh
```

The command creates a temporary local feed and NuGet cache, packs a matching LiteDB/runtime-analyzer pair, verifies the analyzer archive, restores an external package consumer, and publishes/runs that consumer as Native AOT. It needs the Linux Native AOT toolchain and uses NuGet.org only for SDK Native AOT and linker tooling; the LiteDB package pair itself is created in the local feed.
