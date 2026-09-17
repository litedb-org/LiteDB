using System;
using System.IO;

using LiteDB.Generated;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static LiteDB.AotTests.SourceGeneratedMappingTestHelper;

namespace LiteDB.AotTests
{
    public sealed class SourceGeneratedMappingRegressionTests
    {
        [TestMethod]
        public void GetGeneratedCollection_AssignsAutoIdsToNullableIdTypes()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var intRecord = new NullableIntIdRecord();
                var longRecord = new NullableLongIdRecord();
                var guidRecord = new NullableGuidIdRecord();

                database.GetGeneratedCollection<NullableIntIdRecord>("nullableIntIds").Insert(intRecord);
                database.GetGeneratedCollection<NullableLongIdRecord>("nullableLongIds").Insert(longRecord);
                database.GetGeneratedCollection<NullableGuidIdRecord>("nullableGuidIds").Insert(guidRecord);

                Assert.IsTrue(intRecord.Id.HasValue && intRecord.Id.Value > 0);
                Assert.IsTrue(longRecord.Id.HasValue && longRecord.Id.Value > 0);
                Assert.IsTrue(guidRecord.Id.HasValue && guidRecord.Id.Value != Guid.Empty);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_QueryUsesGeneratedMappingForInheritedMember()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<InheritedRecord>("inheritedQuery");
                collection.Insert(new InheritedRecord
                {
                    BaseId = 1,
                    BaseName = "generated-base",
                    DerivedName = "derived"
                });

                var result = collection.Query()
                    .Where(record => record.BaseName == "generated-base")
                    .SingleOrDefault();

                Assert.IsNotNull(result);
                Assert.AreEqual("derived", result.DerivedName);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
