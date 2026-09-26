using System;
using System.IO;
using LiteDB.Generated;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class GeneratedMapperOwnershipTests
    {
        [TestMethod]
        public void DefaultDatabaseMapper_PreservesGeneratedRegistrationsAndIsolatesMetadata()
        {
            var previous = BsonMapper.Global;
            var path = SourceGeneratedMappingTestHelper.GetDatabasePath();
            try
            {
                var global = new BsonMapper();
                LiteDbGeneratedMappings.Register(global);
                BsonMapper.Global = global;
                using (var database = new LiteDatabase(path))
                using (var other = new LiteDatabase(new MemoryStream()))
                {
                    var collection = database.GetGeneratedCollection<GeneratedRecord>("records");
                    var otherCollection = other.GetGeneratedCollection<GeneratedRecord>("records");
                    Assert.AreNotSame(global, database.Mapper);
                    Assert.AreNotSame(collection.EntityMapper, otherCollection.EntityMapper);
                    Assert.AreNotSame(collection.EntityMapper.Id, otherCollection.EntityMapper.Id);
                    collection.Insert(new GeneratedRecord { Name = "preserved" });
                    Assert.AreEqual("preserved", collection.Query().Where(x => x.Id == 1).Select(x => x.Name).First());
                    otherCollection.EntityMapper.Id.AutoId = false;
                    Assert.IsTrue(collection.EntityMapper.Id.AutoId);
                }
                using var reopened = new LiteDatabase(path);
                Assert.AreEqual("preserved", reopened.GetGeneratedCollection<GeneratedRecord>("records").FindById(1).Name);
            }
            finally
            {
                BsonMapper.Global = previous;
                File.Delete(path);
            }
        }

        [TestMethod]
        public void DefaultDatabaseMapper_PreservesGeneratedConfigurationRejection()
        {
            var previous = BsonMapper.Global;
            try
            {
                var global = new BsonMapper();
                LiteDbGeneratedMappings.Register(global);
                global.RegisterType<Uri>(value => value.ToString(), value => new Uri(value.AsString));
                BsonMapper.Global = global;
                using var database = new LiteDatabase(new MemoryStream());
                Assert.ThrowsException<InvalidOperationException>(() =>
                    database.GetGeneratedCollection<GeneratedRecord>("records"));
            }
            finally
            {
                BsonMapper.Global = previous;
            }
        }
    }
}
