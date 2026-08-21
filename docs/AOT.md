# Native AOT and source-generated entity mapping

LiteDB supports an opt-in typed-collection path for applications that publish with **Native AOT** and need to avoid runtime discovery of application model members. The path uses a C# incremental source generator to emit `EntityMapper` definitions at compile time. It replaces the former `LiteAotDatabase` wrapper and manual `EntityMapper` construction.

LiteDB itself targets `netstandard2.0` and `net8.0`; its AOT compatibility analysis is enabled only for the `net8.0` target. The consuming application must target **.NET 8 or later** when it publishes with Native AOT.

## Configure the consuming project

The generator is currently an in-repository analyzer project. A consuming project in this repository references it as an analyzer only; it is not a runtime dependency and adds no runtime assembly reference.

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

`Register` must run before `GetGeneratedCollection<T>`. Calling it twice for the same mapper throws `InvalidOperationException`. `GetGeneratedCollection<T>` also throws when no generated map for `T` has been registered. This is intentional: it prevents the generated path from silently falling back to automatic runtime mapping.

## Supported model subset

The first source-generated mapping slice is intentionally narrow. The generator accepts a directly annotated, top-level, non-abstract, non-generic `public` or `internal` class with an accessible parameterless constructor. It can flatten supported public read/write instance properties declared by that class and its public or internal, top-level, non-generic base classes. A base class may be abstract; only the concrete derived class is marked and directly constructed.

`[BsonId]`, `[BsonField]`, and `[BsonIgnore]` are supported on inherited and directly declared properties. The generator also applies LiteDB's `Id` and `<TypeName>Id` ID conventions. A `List<string>` is serialized and materialized through generated loops rather than reflection-based collection activation. An unannotated public getter-only property that is not an ID convention is treated as a computed projection and is excluded from persistence; mark it with `[BsonIgnore]` if explicit documentation is preferred. A getter-only `[BsonId]`, `[BsonField]`, or conventional ID remains unsupported because the generated path cannot hydrate it. Member hiding, duplicate effective BSON field names, and multiple resolved IDs across an inheritance hierarchy produce `LDBSG001` rather than an ambiguous map.

| Supported | Not supported by the generated path |
|---|---|
| Scalar properties and nullable scalar value types, enums, `byte[]`, `DateTime`, `DateTimeOffset`, `DateTimeOffset?`, `Guid`, and `ObjectId` | Parameterized and `[BsonCtor]` constructors |
| `List<string>`, rank-one `string[]`, and `Dictionary<string, object?>` containing BSON-native dynamic values | Other array element types, other list element types, sets, typed or custom dictionaries, nested entities, and `BsonRef` |
| `[BsonId]`, `[BsonField]`, and `[BsonIgnore]` on direct and inherited properties; unannotated computed getter-only projections | Fields, persisted getter-only or init-only properties, member hiding, duplicate BSON field names or IDs, generic or nested model/base classes |
| Explicit collection names | Default collection-name resolution and runtime mapper callbacks |

### DateTimeOffset representation

The generated path preserves `DateTimeOffset` and nullable `DateTimeOffset` values as an embedded BSON document with two `Int64` fields: `DateTime` contains `DateTimeOffset.Ticks` and `Offset` contains `DateTimeOffset.Offset.Ticks`. This retains the original offset and 100-nanosecond ticks; it does not use LiteDB's BSON `DateTime` representation.

The outer DateTimeOffset property is therefore a BSON document rather than a sortable BSON date. Applications that need an index over its stored components must use an explicit BSON expression for the `DateTime` or `Offset` child field and choose the ordering semantics appropriate to their domain.

### String array representation

Rank-one `string[]` properties use a BSON Array and generated indexed copy loops. With `BsonMapper.SerializeNullValues` enabled, a null string array is persisted as BSON Null and deserializes as null; an empty array remains a non-null, empty `string[]`. Other array element types remain unsupported.

### Nullable scalar values

A nullable scalar value type is supported when its underlying value type is supported. With the default mapper setting, null members retain LiteDB's normal omission behavior. When `BsonMapper.SerializeNullValues` is enabled, a nullable scalar `null` is persisted as BSON Null and deserializes as `null` through the generated mapping path.

### Dynamic dictionary representation

`Dictionary<string, object?>` is supported as a constrained BSON-native dynamic document. The generated path stores it as a BSON Document. It accepts null, `BsonValue`, BSON-native scalar values, and recursively nested dictionaries or arrays/enumerables. On materialization, nested BSON documents become `Dictionary<string, object?>` and nested BSON arrays become `object?[]`; other BSON values retain their raw CLR values.

With the default mapper setting, a null dictionary member follows LiteDB's normal omission behavior. When `BsonMapper.SerializeNullValues` is enabled, a null dictionary is persisted as BSON Null and deserializes as `null`. Arbitrary CLR objects, including `DateTimeOffset` values inside the dictionary, are intentionally rejected with `InvalidOperationException`. Typed dictionaries, dictionary interfaces, custom dictionary types, and arbitrary nested entity graphs remain unsupported by the generated path.

Unsupported annotated shapes produce an `LDBSG001` build diagnostic. Use the existing runtime-mapped LiteDB APIs for models outside the supported generated subset.

## Contributor validation

`LiteDB.AotTests` exercises generated registration, IDs, field and ignore attributes, scalar and nullable scalar values, `DateTimeOffset` values, inherited properties, computed projections, `List<string>`, `string[]`, and dynamic-dictionary round trips. `LiteDB.AotSmokeTests` publishes and runs a Native AOT executable with generated scalar, nullable scalar, list, string-array, DateTimeOffset, inherited-property, computed-projection, and dynamic-dictionary workflows alongside document, query, and stream scenarios.

Run the focused tests with:

```bash
dotnet test LiteDB.AotTests/LiteDB.AotTests.csproj -c Release
```

Run the Native AOT smoke test with:

```bash
dotnet publish LiteDB.AotSmokeTests/LiteDB.AotSmokeTests.csproj -c Release -r linux-x64 --self-contained true
./LiteDB.AotSmokeTests/bin/Release/net8.0/linux-x64/publish/LiteDB.AotSmokeTests
```
