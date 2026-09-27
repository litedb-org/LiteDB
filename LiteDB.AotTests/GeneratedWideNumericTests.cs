using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB.Generated;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests;

[TestClass]
public sealed class GeneratedWideNumericTests
{
    [TestMethod]
    public void Unsigned_scalars_preserve_bits_in_projection_aggregates_and_file_ids_after_reopen()
    {
        var path = SourceGeneratedMappingTestHelper.GetDatabasePath();
        try
        {
            using (var database = new LiteDatabase(path, SourceGeneratedMappingTestHelper.CreateGeneratedMapper()))
            {
                database.GetGeneratedCollection<NativeScalarRecord>("scalars").Insert(new NativeScalarRecord
                {
                    Id = 1, UnsignedLong = ulong.MaxValue, UnsignedInteger = uint.MaxValue
                });
                database.GetGeneratedCollection<NullableScalarBoundaryRecord>("nullable").Insert(
                    new NullableScalarBoundaryRecord { Id = 1, UnsignedLong = ulong.MaxValue });
                database.GetGeneratedCollection<NullableScalarBoundaryRecord>("nullable").Insert(
                    new NullableScalarBoundaryRecord { Id = 2 });
                using var input = new MemoryStream(new byte[] { 1, 2, 3 });
                database.GetStorage<ulong>().Upload(ulong.MaxValue, "wide.bin", input);
                database.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 7, ["value"] = "preserved" });
            }
            using (var database = new LiteDatabase(path, SourceGeneratedMappingTestHelper.CreateGeneratedMapper()))
            {
                var collection = database.GetGeneratedCollection<NativeScalarRecord>("scalars");
                Assert.AreEqual(-1L, database.GetCollection("scalars").FindById(1)["UnsignedLong"].AsInt64);
                Assert.AreEqual(ulong.MaxValue, collection.Query().Select(x => x.UnsignedLong).Single());
                Assert.AreEqual(ulong.MaxValue, collection.Min(x => x.UnsignedLong));
                Assert.AreEqual(ulong.MaxValue, collection.Max(x => x.UnsignedLong));
                Assert.AreEqual(uint.MaxValue, collection.Query().Select(x => x.UnsignedInteger).Single());
                var nullable = database.GetGeneratedCollection<NullableScalarBoundaryRecord>("nullable");
                Assert.AreEqual((ulong?)ulong.MaxValue, nullable.Query().Where(x => x.Id == 1).Select(x => x.UnsignedLong).Single());
                Assert.IsNull(nullable.Query().Where(x => x.Id == 2).Select(x => x.UnsignedLong).Single());
                var storage = database.GetStorage<ulong>();
                Assert.AreEqual(ulong.MaxValue, storage.FindById(ulong.MaxValue).Id);
                using var output = new MemoryStream();
                storage.Download(ulong.MaxValue, output);
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, output.ToArray());
                Assert.AreEqual("preserved", database.GetCollection("unrelated").FindById(7)["value"].AsString);
            }
        }
        finally { File.Delete(path); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Wide_enum_captures_and_dynamic_values_match_runtime_mapping_after_reopen(bool asInteger)
    {
        var path = SourceGeneratedMappingTestHelper.GetDatabasePath();
        var mapper = SourceGeneratedMappingTestHelper.CreateGeneratedMapper();
        mapper.EnumAsInteger = asInteger;
        var values = new object[] { SignedWideState.BeyondInt32, UnsignedWideState.NearMaximum,
            WideUIntState.Maximum, NativeScalarState.Captured, (SignedWideState)long.MinValue };
        var runtime = new BsonMapper { EnumAsInteger = asInteger };
        try
        {
            using (var database = new LiteDatabase(path, mapper))
            {
                var dictionary = new Dictionary<string, object?>();
                for (var i = 0; i < values.Length; i++) dictionary["value" + i] = values[i];
                database.GetGeneratedCollection<DynamicDictionaryRecord>("dynamic").Insert(
                    new DynamicDictionaryRecord { Id = 1, Fields = dictionary });
                database.GetGeneratedCollection<WideEnumRecord>("enums").Insert(new WideEnumRecord
                {
                    Id = 1, Signed = SignedWideState.BeyondInt32, Unsigned = UnsignedWideState.NearMaximum,
                    UInt = WideUIntState.Maximum
                });
            }
            using (var database = new LiteDatabase(path, mapper))
            {
                var raw = database.GetCollection("dynamic").FindById(1)["Fields"].AsDocument;
                var decoded = database.GetGeneratedCollection<DynamicDictionaryRecord>("dynamic").FindById(1);
                for (var i = 0; i < values.Length; i++)
                {
                    var expected = runtime.Serialize(values[i]);
                    Assert.AreEqual(expected.Type, raw["value" + i].Type);
                    Assert.AreEqual(expected, raw["value" + i]);
                    Assert.AreEqual(expected.RawValue, decoded.Fields["value" + i]);
                }
                var signed = SignedWideState.BeyondInt32;
                var unsigned = UnsignedWideState.NearMaximum;
                var uintValue = WideUIntState.Maximum;
                var collection = database.GetGeneratedCollection<WideEnumRecord>("enums");
                collection.EnsureIndex(x => x.Signed);
                collection.EnsureIndex(x => x.Unsigned);
                collection.EnsureIndex(x => x.UInt);
                Assert.AreEqual(1, collection.FindOne(x => x.Signed == signed).Id);
                Assert.AreEqual(1, collection.FindOne(x => x.Unsigned == unsigned).Id);
                Assert.AreEqual(1, collection.FindOne(x => x.UInt == uintValue).Id);
                Assert.AreEqual(unsigned, collection.Query().Select(x => x.Unsigned).Single());
                Assert.IsTrue(collection.DeleteMany(x => x.UInt == uintValue) == 1);
                Assert.AreEqual(1, database.GetCollection("dynamic").Count());
            }
        }
        finally { File.Delete(path); }
    }
}
