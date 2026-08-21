# Native AOT and Explicit Entity Mapping

LiteDB supports a typed collection workflow for applications that publish with **.NET Native AOT** or that must avoid runtime discovery of application model types. Use `LiteAotDatabase` with explicitly registered `EntityMapper` instances instead of the runtime-mapped typed APIs on `LiteDatabase`.

> Native AOT compiles an application to native code at publish time. Runtime reflection over constructors and members can be incompatible with trimming because the required metadata or generated code might not be preserved. [1]

## Configure the consuming application

LiteDB targets `netstandard2.0` and `net8.0`. Native AOT compatibility analysis is enabled only for the `net8.0` target. An application that consumes LiteDB with Native AOT publishing should target `net8.0` or later and enable AOT publishing in its project file.

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

Publish for a specific runtime identifier, for example:

```text
dotnet publish -c Release -r linux-x64
```

Refer to the [official Native AOT deployment guidance][1] for platform prerequisites and publishing options.

## Explicit mapping workflow

Create one complete `EntityMapper` for each entity type used through `LiteAotDatabase`, register every map with a `BsonMapper`, and request collections by an explicit name. `LiteAotDatabase.GetCollection<T>` throws `InvalidOperationException` when no explicit map for `T` has been registered.

The following minimal example maps an entity with scalar members.

```csharp
using LiteDB;

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public static class AotExample
{
    public static void Run()
    {
        var mapper = CreateMapper();

        using var database = new LiteAotDatabase("customers.db", mapper);
        var customers = database.GetCollection<Customer>("customers");
    }

    private static BsonMapper CreateMapper()
    {
        var mapper = new BsonMapper();
        var map = new EntityMapper(typeof(Customer))
        {
            CreateInstance = _ => new Customer()
        };

        map.Members.Add(new MemberMapper
        {
            AutoId = true,
            FieldName = "_id",
            MemberName = nameof(Customer.Id),
            DataType = typeof(int),
            UnderlyingType = typeof(int),
            Getter = entity => ((Customer)entity).Id,
            Setter = (entity, value) => ((Customer)entity).Id = (int)value
        });

        map.Members.Add(new MemberMapper
        {
            FieldName = nameof(Customer.Name),
            MemberName = nameof(Customer.Name),
            DataType = typeof(string),
            UnderlyingType = typeof(string),
            Getter = entity => ((Customer)entity).Name,
            Setter = (entity, value) => ((Customer)entity).Name = (string)value
        });

        mapper.RegisterAotEntityMapper(map);
        return mapper;
    }
}
```

For collection-valued or custom members, supply the appropriate `Serialize` and `Deserialize` delegates in the `MemberMapper`. The repository’s [AOT smoke test][2] contains verified examples for a `List<string>` member.

## Supported AOT surface

| Supported workflow | Notes |
|---|---|
| `LiteAotDatabase(string, BsonMapper)` | Opens a file-backed database with a non-null explicit mapper. |
| `LiteAotDatabase(Stream, BsonMapper, Stream)` | Opens a stream-backed database with a non-null explicit mapper. |
| `GetCollection<T>(string, BsonAutoId)` | Returns a typed collection when an explicit map for `T` is registered. The collection name is required. |
| Explicit `EntityMapper` members | Maps entity construction, fields, getters, setters, and custom serialization behavior without runtime model discovery. |

## APIs outside this AOT workflow

The following APIs rely on runtime model discovery, runtime type construction, or runtime collection-name resolution and are not part of the explicit mapping workflow:

| Do not use for explicitly mapped Native AOT entities | Use instead |
|---|---|
| `LiteDatabase.GetCollection<T>(...)` | `LiteAotDatabase.GetCollection<T>("collection-name")` after registering an `EntityMapper` for `T`. |
| `LiteDatabase.GetCollection<T>()` and `LiteDatabase.GetCollection<T>(BsonAutoId)` | Choose and pass an explicit collection name to `LiteAotDatabase.GetCollection<T>`. |
| `LiteRepository`, `LiteQueryable`, and typed `GetStorage<TFileId>` | These APIs are not exposed through `LiteAotDatabase` and retain runtime-mapping or runtime-type-construction requirements. |
| An unregistered entity type | Register a complete `EntityMapper` before requesting its collection. |

The standard `LiteDatabase` APIs remain available for regular runtime-mapped applications. However, their Native AOT and trimming annotations identify the operations that require runtime discovery or dynamic type construction. Do not suppress those warnings as a substitute for explicit maps.

## Contributor smoke test

`LiteDB.AotSmokeTests` is the repository’s Native AOT executable test application. It targets `net8.0`, sets `PublishAot` to `true`, and treats trimming and AOT warnings as errors. Its scenarios cover document operations and expressions, stream-backed databases, and explicitly mapped typed collections with both scalar and `List<string>` members. [2]

When contributing a change that can affect AOT behavior, publish the smoke-test project for the runtime identifier you need to support:

```text
dotnet publish LiteDB.AotSmokeTests/LiteDB.AotSmokeTests.csproj -c Release -r <RID>
```

Run the native executable from `LiteDB.AotSmokeTests/bin/Release/net8.0/<RID>/publish/`. The application reports `LiteDB deployment smoke test passed.` only after every scenario succeeds. Native AOT publishing requires the platform prerequisites documented by .NET. [1]

## Validate the application

Build and publish the consuming application with Native AOT enabled. Treat trimming and AOT analyzer warnings as issues to resolve, then run a round-trip test for every mapped entity: insert a value, read it through the typed collection, and verify all mapped members. The repository’s AOT smoke test follows this pattern for both scalar and collection-valued members. [2]

## References

[1]: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ "Native AOT deployment"
[2]: ../LiteDB.AotSmokeTests/Program.cs "LiteDB AOT smoke test"
