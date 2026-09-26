using System;
using System.IO;

using LiteDB.Generated;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static LiteDB.AotTests.SourceGeneratedMappingTestHelper;

namespace LiteDB.AotTests
{
    [TestClass]
    public sealed class SourceGeneratedMappingRegressionTests
    {
        [TestMethod]
        public void IdSelection_PrefersExplicitIdThenIdThenDeclaringTypeConvention()
        {
            var mapper = CreateGeneratedMapper();
            using var database = new LiteDatabase(new MemoryStream(), mapper);
            var conventional = new EmployeeWithId { Id = 12, PersonId = 34 };
            var explicitId = new EmployeeWithExplicitId { Id = 12, PersonId = 34, Key = 56 };

            Assert.AreEqual(new BsonValue(12), database.GetGeneratedCollection<EmployeeWithId>("conventional").Insert(conventional));
            Assert.AreEqual(mapper.ToDocument(conventional), database.GetCollection("conventional").FindById(12));
            Assert.AreEqual(new BsonValue(56), database.GetGeneratedCollection<EmployeeWithExplicitId>("explicit").Insert(explicitId));
            Assert.AreEqual(mapper.ToDocument(explicitId), database.GetCollection("explicit").FindById(56));
        }

        [TestMethod]
        public void InheritedConventionalId_CrossReadsAndAutoIdsMatchReflectionMapping()
        {
            var mapper = CreateGeneratedMapper();
            using var database = new LiteDatabase(new MemoryStream(), mapper);
            var generated = database.GetGeneratedCollection<Employee>("employees");
            var ordinary = database.GetCollection<Employee>("employees");
            var employee = new Employee { PersonId = 42, Name = "Ada" };

            Assert.AreEqual(new BsonValue(42), generated.Insert(employee));
            Assert.AreEqual(mapper.ToDocument(employee), database.GetCollection("employees").FindById(42));
            Assert.AreEqual(42, ordinary.FindById(42).PersonId);

            ordinary.Insert(new Employee { PersonId = 43, Name = "Grace" });
            Assert.AreEqual(43, generated.FindById(43).PersonId);
            Assert.AreEqual("Ada", generated.FindOne(x => x.PersonId == 42).Name);

            var autoId = new Employee { Name = "auto" };
            Assert.AreEqual(new BsonValue(44), generated.Insert(autoId));
            Assert.AreEqual(44, autoId.PersonId);
        }

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
