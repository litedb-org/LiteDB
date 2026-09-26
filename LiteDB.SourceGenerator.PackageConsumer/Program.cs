using System;
using System.IO;

using LiteDB;
using LiteDB.Generated;

namespace LiteDB.SourceGenerator.PackageConsumer;

internal static class Program
{
    private static void Main()
    {
        var mapper = new BsonMapper();
        LiteDbGeneratedMappings.Register(mapper);

        using var stream = new MemoryStream();
        using var database = new LiteDatabase(stream, mapper);
#if RUNTIME_MAPPING_WARNING_PROBE
        database.GetStorage<object>();
        ((ILiteDatabase)database).GetStorage<object>();
        _ = new LiteStorage<object>(database, "files", "chunks");
        DefaultTypeNameBinder.Instance.GetType("UnpreservedModel, Consumer");
        ((ITypeNameBinder)DefaultTypeNameBinder.Instance).GetType("UnpreservedModel, Consumer");
#endif
        var collection = database.GetGeneratedCollection<PackagedGeneratedRecord>("records");

        collection.Insert(new PackagedGeneratedRecord { Name = "package-consumer" });
        var record = collection.FindById(1);

        if (record?.Name != "package-consumer")
        {
            throw new InvalidOperationException("The packaged source generator did not complete a real LiteDB round trip.");
        }

        Console.WriteLine("[PASS] Packaged LiteDB.SourceGenerator restore, generation, registration, Native AOT publish, and real LiteDB round trip succeeded.");
    }
}
