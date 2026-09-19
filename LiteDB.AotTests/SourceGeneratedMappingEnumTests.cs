using System;
using System.IO;

using LiteDB.Generated;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static LiteDB.AotTests.SourceGeneratedMappingTestHelper;

namespace LiteDB.AotTests
{
    [TestClass]
    public sealed class SourceGeneratedMappingEnumTests
    {
        [TestMethod]
        public void GetGeneratedCollection_RoundTripsWideEnumsAsInt64()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper { EnumAsInteger = true };
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<WideEnumRecord>("wideEnums");
                collection.Insert(new WideEnumRecord
                {
                    Id = 1,
                    Signed = SignedWideState.BeyondInt32,
                    Unsigned = UnsignedWideState.NearMaximum,
                    NullableSigned = SignedWideState.BeyondInt32
                });

                var document = database.GetCollection("wideEnums").FindById(1);
                Assert.IsTrue(document[nameof(WideEnumRecord.Signed)].IsInt64);
                Assert.AreEqual(5_000_000_000L, document[nameof(WideEnumRecord.Signed)].AsInt64);
                Assert.IsTrue(document[nameof(WideEnumRecord.Unsigned)].IsInt64);
                Assert.AreEqual(-4L, document[nameof(WideEnumRecord.Unsigned)].AsInt64);

                var result = collection.FindById(1);
                Assert.IsNotNull(result);
                Assert.AreEqual(SignedWideState.BeyondInt32, result.Signed);
                Assert.AreEqual(UnsignedWideState.NearMaximum, result.Unsigned);
                Assert.AreEqual(SignedWideState.BeyondInt32, result.NullableSigned);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_DeserializesCompatibleInt64EnumValues()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper { EnumAsInteger = true };
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                database.GetCollection("wideEnumCompatibility").Insert(new BsonDocument
                {
                    ["_id"] = 1,
                    [nameof(WideEnumRecord.Signed)] = 5_000_000_000L,
                    [nameof(WideEnumRecord.Unsigned)] = -4L,
                    [nameof(WideEnumRecord.NullableSigned)] = 5_000_000_000L
                });

                var collection = database.GetGeneratedCollection<WideEnumRecord>("wideEnumCompatibility");
                var result = collection.FindById(1);

                Assert.IsNotNull(result);
                Assert.AreEqual(SignedWideState.BeyondInt32, result.Signed);
                Assert.AreEqual(UnsignedWideState.NearMaximum, result.Unsigned);
                Assert.AreEqual(SignedWideState.BeyondInt32, result.NullableSigned);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
