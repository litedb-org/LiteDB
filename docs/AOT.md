# Native AOT and source-generated entity mapping

LiteDB supports an opt-in typed-collection path for applications that publish with **Native AOT** and need to avoid runtime discovery of application model members. The path uses a C# incremental source generator to emit `EntityMapper` definitions at compile time.

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

`LiteDbGeneratedMappings` is internal to each consuming assembly. A model library should expose its own public registration method that calls `LiteDbGeneratedMappings.Register`; applications call each library's method. This lets multiple generated model libraries coexist without conflicting public type names.

`GetGeneratedCollection<T>` is a member of `LiteDatabase`, not a new requirement on `ILiteDatabase`. Existing interface implementations and decorators can retain their original members. Constructing `LiteStorage<TFileId>` over a custom interface implementation retains its typed-collection path and the constructor's trim/AOT warnings.

The generator automatically registers a direct execution map for every supported model. Supported values include scalars, `List<string>`, rank-one `string[]`, and `Dictionary<string, object?>`; supported scalars include Boolean, the signed and unsigned integer widths, Single, Double, Decimal, Char, String, enum, `byte[]`, `DateTime`, `DateTimeOffset`, `Guid`, `ObjectId`, and nullable value-type forms of those values. `LiteDbGeneratedMappings.Register(mapper)` is the only registration call required; consumers do not call `RegisterGeneratedExecutionMap` manually. Direct maps perform generated document conversion and reproduce `SerializeNullValues`, `TrimWhitespace`, `EmptyStringToNull`, and `EnumAsInteger`; every other mapper-shaping setting is rejected before data access. Generated smoke fixtures guard the broad `ToDocument(Type, object)` and `ToObject(Type, BsonDocument)` methods so an accidental fallback is observable and build-breaking. Ordinary `GetCollection<T>` remains the reflection-capable LiteDB API. See [SourceGeneratorPackage.md](SourceGeneratorPackage.md) for package-version policy, contributor package checks, and release requirements.

## Supported model subset

The supported model subset is intentionally narrow. The generator accepts a directly annotated, top-level, sealed, non-generic `public` or `internal` class or mutable record class with an accessible parameterless constructor. Sealing the annotated type prevents a generated collection from receiving a derived runtime type whose additional members are absent from the generated map. The generator can flatten supported public read/write instance properties declared by the annotated class and its public or internal, top-level, non-generic base classes. Compiler-generated record members, such as `EqualityContract`, are not persisted. A base class may be abstract; only the sealed concrete derived class is marked and directly constructed.

`[BsonId]`, `[BsonField]`, and `[BsonIgnore]` are supported on inherited and directly declared properties. For a virtual override chain, the generator maps only the most-derived property and resolves a mapping attribute from that declaration first, then from the nearest overridden declaration. ID selection follows the reflection mapper's precedence: `[BsonId]`, then `Id`, then `<DeclaringTypeName>Id`. An inherited `Person.PersonId` is therefore the ID of an annotated `Employee : Person` too. A `List<string>` is serialized and materialized through generated loops rather than reflection-based collection activation. An unannotated public getter-only property that is not an ID convention is treated as a computed projection and is excluded from persistence; mark it with `[BsonIgnore]` if explicit documentation is preferred. A getter-only `[BsonId]`, `[BsonField]`, or conventional ID remains unsupported because the generated path cannot hydrate it. Mapped `new`-hidden members, case-insensitive duplicate BSON field names, and multiple IDs at the same precedence produce `LDBSG003`. `[BsonRef]` properties produce `LDBSG002` even when the property type would otherwise be supported.

| Supported | Not supported by the generated path |
|---|---|
| Scalar properties and nullable scalar value types, enums, `byte[]`, `DateTime`, `DateTimeOffset`, `DateTimeOffset?`, `Guid`, and `ObjectId` | Parameterized and `[BsonCtor]` constructors |
| `List<string>`, rank-one `string[]`, and `Dictionary<string, object?>` containing BSON-native dynamic values | Other array element types, other list element types, sets, typed or custom dictionaries, nested entities, and `BsonRef` |
| `[BsonId]`, `[BsonField]`, and `[BsonIgnore]` on direct and inherited properties; mutable record classes; unannotated computed getter-only projections; virtual override chains | Fields, persisted getter-only or init-only properties, mapped `new`-hidden members, duplicate BSON field names or IDs, generic or nested model/base classes |
| Explicit collection names | Default collection-name resolution and runtime mapper callbacks |

### DateTimeOffset representation

Ordinary and source-generated mapping write `DateTimeOffset` and nullable `DateTimeOffset` values as BSON `DateTime` values containing the UTC instant. BSON DateTime precision is one millisecond, so the original offset and any sub-millisecond ticks are not retained. Reads therefore materialize a UTC `DateTimeOffset`, consistently across ordinary managed, generated managed, and Native AOT execution paths.

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

`GetCollection<T>`, `LiteRepository`, `BsonMapper.Entity<T>()`, `BsonMapper.GetExpression`, and `BsonMapper.ToDocument`/`ToObject`/`Serialize`/`Deserialize` use runtime model mapping. They carry `RequiresUnreferencedCode` (and `RequiresDynamicCode` where types are constructed at runtime), so calling them from a trimmed or Native AOT application produces a diagnostic at the call site. `GetCollection(string)` (the `BsonDocument` API), SQL through `Execute`, string-ID `FileStorage`, and the engine features (transactions, encryption, shared mode, rebuild, pragmas, vector search) are validated published. File storage maps its own `LiteFileInfo<TFileId>` model with hand-written code, but an arbitrary custom ID can still require runtime mapping. Both `GetStorage<TFileId>` and the public `LiteStorage<TFileId>` constructor therefore carry trim/AOT warnings, including calls through `ILiteDatabase`. Those warnings are conservative: scalar IDs, flat IDs and explicitly registered converters can work, but require application-specific qualification before suppression. A LINQ expression on a `BsonDocument` collection follows the same rule as one on a generated collection: captured values must be BSON-native (or have a registered converter), and a captured application object throws `NotSupportedException` instead of being mapped through reflection. This applies in every runtime, not only when published.

| Diagnostic | Meaning | Typical remediation |
| --- | --- | --- |
| `LDBSG001` | Invalid source-generated model or inheritance hierarchy | Use a top-level, sealed, public/internal concrete class with an accessible parameterless constructor and supported base classes. |
| `LDBSG002` | Invalid source-generated property | Change, ignore, or remove an unsupported, inaccessible, indexed/static, or persisted getter-only property. |
| `LDBSG003` | Conflicting source-generated mapping | Remove duplicate mapped member names, IDs, conventional IDs, or effective BSON field names across the hierarchy. |

## Compatibility boundaries and known limits

These limits need to be considered when selecting the API and validating an application. The document-query change also affects untrimmed applications.

**1. Custom file IDs require trim/AOT warnings.** A class used as file ID has its top-level members preserved; nested types do not. Ignoring the warnings on `GetStorage<TFileId>` or the `LiteStorage<TFileId>` constructor can cause data loss:

```csharp
class FileKey { public int Tenant { get; set; } public Address Home { get; set; } }
class Address { public string City { get; set; } }

// normal app : {"Tenant":1,"Home":{"City":"Vienna"}}
// trimmed app: {"Tenant":1,"Home":{}}        <- two different ids can become the same id
```

Use the warning-free `FileStorage` property for string IDs. For a custom ID, register a converter for the complete ID and verify a published round trip before narrowly suppressing the warnings. Preserve the existing BSON shape when reading existing files:

```csharp
mapper.RegisterType<FileKey>(
    key  => new BsonDocument { ["Tenant"] = key.Tenant, ["Home"] = new BsonDocument { ["City"] = key.Home.City } },
    bson => new FileKey { Tenant = bson["Tenant"].AsInt32, Home = new Address { City = bson["Home"]["City"].AsString } });
```

The published smoke tests separately qualify integer IDs and the flat `AotFileKey` model, using member-level suppressions with those exact invariants. They do not establish safety for arbitrary custom IDs. The package-consumer gate also verifies that all three public entry points reject an unsuppressed custom-ID call with both `IL2026` and `IL3050`.

Persisted type-name lookup through `DefaultTypeNameBinder.GetType` or `ITypeNameBinder.GetType` also reports `IL2026`: a type named only by a stored string may have been removed by trimming. Implementations of `ITypeNameBinder.GetType` need a matching annotation. The package gate verifies warnings on both direct and interface calls.

**2. A query on a `BsonDocument` or generated collection does not accept your own objects as values.** This one applies to every application. Convert them first; the query itself stays the same:

```csharp
object[] owners = { new Person { Name = "Ada" } };
col.Find(x => owners.Contains(x["owner"]));                 // NotSupportedException

var docs = owners.Cast<Person>().Select(p => new BsonDocument { ["Name"] = p.Name }).ToArray();
col.Find(x => docs.Contains(x["owner"]));                   // works
```

The typed API is not affected: `db.GetCollection<Car>().Find(x => owners.Contains(x.Owner))` works as before.

**3. The typed reflection API is not part of the contract.** `GetCollection<T>`, `LiteRepository`, `BsonMapper.ToDocument`/`ToObject` and friends report a trim or AOT warning where you call them. Simple classes usually still work published; members that are collections of value types (`Dictionary<string, int>`, `HashSet<int>`, ...) often do not, because their code has to be built at run time. Use `[BsonSourceGenerated]` models or the `BsonDocument` API instead.

**4. Object initializers and anonymous types in lambdas warn in your code.** `x => new Customer { Name = x.Name }` and `x => new { x.Name }` make the C# compiler emit calls that the trimmer flags with `IL2026` at your call site. The object-initializer form works at run time for generated types; anonymous projections are not supported on generated collections.

**5. `InvariantGlobalization=true` cannot open a data file with a culture collation** such as `en-US/IgnoreCase`. It throws `CultureNotFoundException`.

**6. Queries that evaluate an expression for every document are slower**, roughly 1.2 to 2 times once warm, because expressions are interpreted instead of compiled. Index seeks are not affected. See "Performance".

**7. .NET iOS Mono AOT, .NET iOS NativeAOT, and Unity IL2CPP are verified on a
physical iPhone.** See the next section and the
[iOS AOT validation report](ios-aot-validation.md).

## Mono full AOT (iOS, Mac Catalyst, tvOS)

Native AOT and Mono full AOT are different runtimes. Both lack dynamic code, so both run LiteDB's expression trees through the `System.Linq.Expressions` interpreter, but they differ in how the interpreter hands back a delegate. The interpreter has prebuilt thunks only for delegates with at most two parameters and builds every other delegate with `Reflection.Emit`. Native AOT replaces that step with a runtime service; Mono full AOT does not, so a delegate with more parameters fails with `Attempting to JIT compile method ... while running in aot-only mode` ([#2804](https://github.com/litedb-org/LiteDB/issues/2804)). Every `BsonExpression` used a five-parameter delegate, which is why LiteDB failed on iOS on first use.

When `RuntimeFeature.IsDynamicCodeSupported` is `false`, LiteDB therefore compiles each expression to a one-parameter delegate over reference types and adapts it to the five-parameter form in ordinary code. The entity mapper's getter, setter, and constructor delegates already have at most two parameters. On a runtime with a JIT nothing changes.

What is verified, and what is not:

- `ExpressionsWithoutDynamicCode_Tests` reads the interpreter's own count of emitted thunks. A control test shows that interpreting a five-parameter delegate emits one; with the one-parameter path LiteDB emits none. This proves the mechanism behind #2804 no longer applies.
- Under Native AOT the one-parameter path is the only path, so the whole Native AOT gate (expression sweep, SQL sweep, the test suite) runs on it.
- A .NET 10 `iossimulator-arm64` application was built with Mono full AOT and full trimming, installed, and run in an ARM64 iOS Simulator. The runtime reported both dynamic-code flags as `false`, automatically selected the one-parameter path, and passed the document, generated-mapping, engine, expression, SQL, vector, and file-storage smoke scenarios.
- A signed .NET 10 `ios-arm64` Release application was installed and run on an iPhone 13 Pro Max with iOS 26.7 using Mono full AOT, `UseInterpreter=false`, and no `MtouchInterpreter` fallback. Its bundle contained 24 Mono AOT-data files, including LiteDB, both dynamic-code flags were `false`, and the complete smoke workload passed with exit code 0.
- A clean `ios-arm64` NativeAOT build of the same application contained no managed DLLs or Mono AOT-data files. It passed the same complete workload on the physical iPhone with both dynamic-code flags `false` and exit code 0.
- A Unity 6 ARM64 iOS Simulator player was built with IL2CPP and High managed stripping, installed, and run. Its document, index, expression, SQL, and persistence workload passed with IL2CPP's default delegate path and with LiteDB's one-parameter path forced on. Unity did not expose `RuntimeFeature.IsDynamicCodeSupported`, so the one-parameter path was not selected automatically.
- The same forced-path Unity player was exported for iPhoneOS, signed, installed, and run on an iPhone 13 Pro Max with iOS 26.7. Its Release IL2CPP workload passed on the physical device.
- The reproducible mobile runs used Xcode 27. The .NET physical-device builds disabled workload version validation because the installed workload expects Xcode 26.6. The original #2804 reproducer and a controlled PR-base comparison remain open; the complete current-commit smoke suite is verified without an interpreter fallback.

The complete setup, observed output, limitations, and next steps are recorded in the
[iOS AOT validation report](ios-aot-validation.md).

The checked-in mobile fixtures can be rerun with:

```bash
./scripts/validate-ios-aot.sh simulator
./scripts/validate-unity-ios-aot.sh simulator-all
```

Physical-device modes require the device and signing environment variables documented
in the validation report. Both wrappers retain their logs and generated output under
`artifacts/` and fail unless the installed application emits its runtime PASS marker.

## Performance

LiteDB compiles every `BsonExpression` (filters, projections, index expressions, LINQ translations) to a delegate. A Native AOT application has no JIT, so these expressions run through the `System.Linq.Expressions` interpreter instead. `LiteDB.AotBenchmark` runs the same workloads as a JIT application and as a Native AOT binary:

```bash
dotnet publish LiteDB.AotBenchmark/LiteDB.AotBenchmark.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=false -o artifacts/aot-benchmark/jit
dotnet publish LiteDB.AotBenchmark/LiteDB.AotBenchmark.csproj -c Release -r win-x64 --self-contained true -o artifacts/aot-benchmark/native-aot
```

Indicative figures, from one machine (win-x64, .NET 8.0.30, 100,000 documents, median of five runs, milliseconds). "Cold" is the first execution in the process, which for the JIT build includes JIT compilation; "warm" is the best of three further executions.

| Workload | JIT | Native AOT | AOT / JIT |
| --- | ---: | ---: | ---: |
| Insert 100,000 documents | 1338 | 791 | 0.59 |
| Create an index | 557 | 877 | 1.57 |
| Index seek, 100 queries (cold / warm) | 208 / 185 | 122 / 114 | 0.59 / 0.62 |
| Full scan, simple predicate (cold / warm) | 201 / 100 | 127 / 117 | 0.63 / 1.17 |
| Full scan, compound predicate (cold / warm) | 167 / 123 | 157 / 153 | 0.94 / 1.24 |
| Full scan, array predicate (cold / warm) | 147 / 126 | 199 / 195 | 1.35 / 1.55 |
| Projection over all documents (cold / warm) | 166 / 121 | 257 / 240 | 1.55 / 1.98 |
| `GROUP BY` with aggregates | 268 | 344 | 1.28 |
| `UpdateMany` with an expression | 279 | 327 | 1.17 |
| Create 5,000 distinct expressions | 2342 | 55 | 0.02 |
| Generated collection, insert 100,000 | 480 | 849 | 1.77 |
| Generated collection, read all (cold / warm) | 96 / 84 | 136 / 125 | 1.42 / 1.49 |
| Generated collection, LINQ scan (cold / warm) | 133 / 120 | 217 / 211 | 1.63 / 1.76 |

In short: once warm, a query that evaluates an expression for every document is roughly 1.2 to 2 times slower as Native AOT, because the expression is interpreted. Index seeks are unaffected, and anything dominated by first use (start-up, the first execution of a query, building many distinct expressions) is faster, because nothing has to be compiled at run time. Prefer indexed predicates in Native AOT applications for the same reason as everywhere else, just more so.

## Contributor validation

`LiteDB.AotTests` exercises generated registration, generated scalar conversion with option-sensitive BSON golden documents and ordinary/direct cross-reads, mutable record classes, IDs, field and ignore attributes, `DateTimeOffset` values and cross-path reads, inherited and overridden properties, computed projections, `List<string>`, `string[]`, and dynamic-dictionary round trips. `LiteDB.AotSmokeTests` covers one published scenario per generated-mapping feature family, plus document, query, and stream scenarios.

The smoke project is also the feature-parity contract between publish modes. The parity script publishes it four times and runs every result:

| Mode | Purpose |
| --- | --- |
| `regular` | Untrimmed reference behavior. |
| `trimmed` | Trimmed managed single-file application. |
| `native-aot` | Native AOT, compiling only what the scenarios reach, as a real application would. |
| `native-aot-whole-library` | Native AOT with the whole LiteDB assembly rooted, so the compiler analyses every method of the library and not only the code a scenario happens to call. |

The gate fails when any publish log contains a trim or AOT diagnostic (`ILxxxx`), whatever its code, and when any transcript differs from the regular one. Scenarios write the values they observe into the transcript (`SmokeAssert.Report`), so the comparison covers results and not only the absence of exceptions. A new smoke scenario therefore expands all four gates together rather than relying on independently maintained test lists.

Besides generated mappings, the scenarios cover the engine through the document API: explicit transactions and durability across reopen, password encryption, checkpoint, pragmas and rebuild, a culture-specific collation, shared-mode connections, parallel writers, vector search, and LINQ over `BsonDocument`. An expression sweep executes every registered `BsonExpression` method, function, and operator once; it enumerates LiteDB's own method tables and fails when a registered method has no sweep entry, because published as Native AOT every expression runs through the `System.Linq.Expressions` interpreter. A SQL sweep runs every statement kind, every `SELECT` clause, and every system collection through `Execute`. Generated-collection scenarios run on a mapper whose runtime-mapping hooks all throw, so a silent fallback into reflection stops the run in every mode. CI runs the gate on Linux (x64, arm64, and Alpine/musl), Windows, and macOS for `net8.0` and `net10.0`.

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

Run the existing test suite as a Native AOT binary with:

```bash
./scripts/validate-aot-test-suite.sh
```

`LiteDB.AotTestHost` compiles the sources of `LiteDB.Tests` into a console application with a small reflection-based xunit runner, because no test framework runs xunit v2 tests under Native AOT. The script runs the suite as a regular JIT application and as a Native AOT binary and compares the outcome of every test. A test that passes under the JIT and fails as Native AOT must be listed in `LiteDB.AotTestHost/known-aot-differences.tsv` with its category; any other such test fails the gate. Counts depend on the current upstream suite, runtime, and platform; the run summary reports them together with any baseline failures and undocumented differences.

The reflection-based test host preserves the test and LiteDB assemblies plus the assertion helpers declared in `LiteDB.AotTestHost/TestRoots.rd.xml`. These roots let existing private-reflection and collection assertions execute; they are test infrastructure, not a preservation requirement imposed on consumers. Minimal consumer trimming remains independently verified by the feature-parity applications. Reflection-based model mapping, runtime type emission, and assertion helpers that require unsupported generic code remain documented differences. Published test output also contains recovery fixtures and the managed cross-process MVCC probe.

Run the package-boundary Native AOT validation with:

```bash
./scripts/validate-source-generator-package-consumer.sh
```

The command creates a temporary local feed and NuGet cache, packs a matching LiteDB/runtime-analyzer pair, verifies the analyzer archive, restores an external package consumer, and publishes/runs that consumer as Native AOT. It needs the Linux Native AOT toolchain and uses NuGet.org only for SDK Native AOT and linker tooling; the LiteDB package pair itself is created in the local feed.
