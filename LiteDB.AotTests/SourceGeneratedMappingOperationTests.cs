using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using LiteDB.Engine;
using LiteDB.Generated;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static LiteDB.AotTests.SourceGeneratedMappingTestHelper;

namespace LiteDB.AotTests
{
    [TestClass]
    public sealed class SourceGeneratedMappingOperationTests
    {
        [TestMethod]
        public void GetGeneratedCollection_GeneratedScalarMap_ExecutesExplicitIdAndBatchWritesWithoutMapperFallback()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<GeneratedScalarRecord>("generatedScalarBatchWrites");
                var explicitInsert = new GeneratedScalarRecord { Id = 900, Name = "explicit-insert", Score = 1 };
                collection.Insert(41, explicitInsert);

                Assert.AreEqual(900, explicitInsert.Id);
                Assert.AreEqual("explicit-insert", collection.FindById(41)?.Name);

                var inserted = new[]
                {
                    new GeneratedScalarRecord { Name = "batch-first", Score = 2 },
                    new GeneratedScalarRecord { Name = "batch-second", Score = 3 }
                };
                Assert.AreEqual(2, collection.Insert(inserted));
                Assert.AreNotEqual(0, inserted[0].Id);
                Assert.AreNotEqual(0, inserted[1].Id);
                Assert.AreNotEqual(inserted[0].Id, inserted[1].Id);

#pragma warning disable CS0618
                var bulkInserted = new[]
                {
                    new GeneratedScalarRecord { Name = "bulk-first", Score = 4 },
                    new GeneratedScalarRecord { Name = "bulk-second", Score = 5 }
                };
                Assert.AreEqual(2, collection.InsertBulk(bulkInserted, batchSize: 1));
#pragma warning restore CS0618
                Assert.AreNotEqual(0, bulkInserted[0].Id);
                Assert.AreNotEqual(0, bulkInserted[1].Id);

                inserted[0].Score = 20;
                inserted[1].Score = 30;
                Assert.AreEqual(2, collection.Update(inserted));
                Assert.AreEqual(20, collection.FindById(inserted[0].Id)?.Score);
                Assert.AreEqual(30, collection.FindById(inserted[1].Id)?.Score);

                var explicitUpdate = new GeneratedScalarRecord { Id = 999, Name = "explicit-update", Score = 40 };
                Assert.IsTrue(collection.Update(41, explicitUpdate));
                Assert.AreEqual(999, explicitUpdate.Id);
                var updated = collection.FindById(41);
                Assert.IsNotNull(updated);
                Assert.AreEqual("explicit-update", updated.Name);
                Assert.AreEqual(40, updated.Score);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_InsertBulk_HonorsBatchSizeAndValidatesItBeforeEnumeration()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<GeneratedScalarRecord>("generatedScalarBulkBatches");
                var enumerationCount = 0;

#pragma warning disable CS0618
                var invalid = Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                    collection.InsertBulk(CountEnumeration(), batchSize: 0));
                Assert.AreEqual("batchSize", invalid.ParamName);
                Assert.AreEqual(0, enumerationCount);

                var records = Enumerable.Range(1, 5)
                    .Select(value => new GeneratedScalarRecord { Name = $"bulk-{value}", Score = value })
                    .ToArray();
                Assert.AreEqual(5, collection.InsertBulk(records, batchSize: 2));
#pragma warning restore CS0618

                Assert.AreEqual(5, collection.Count());
                Assert.IsTrue(records.All(record => record.Id != 0));

                IEnumerable<GeneratedScalarRecord> CountEnumeration()
                {
                    enumerationCount++;
                    yield return new GeneratedScalarRecord();
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_ExecutesQueryAggregateAndMutationParityWithoutMapperFallback()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<GeneratedScalarRecord>("generatedParity");
                collection.Insert(new[]
                {
                    new GeneratedScalarRecord { Name = "first", Score = 1 },
                    new GeneratedScalarRecord { Name = "second", Score = 2 },
                    new GeneratedScalarRecord { Name = "third", Score = 3 }
                });

                Assert.AreEqual(3, collection.FindAll().Count());
                Assert.AreEqual("second", collection.Find(record => record.Score >= 2, 0, 1).Single().Name);
                Assert.AreEqual("third", collection.FindOne("Score = @0", 3).Name);
                Assert.AreEqual(2, collection.Count(record => record.Score >= 2));
                Assert.AreEqual(2L, collection.LongCount("Score >= @0", 2));
                Assert.IsTrue(collection.Exists(record => record.Name == "first"));
                Assert.AreEqual(1, collection.Min(record => record.Score));
                Assert.AreEqual(3, collection.Max(record => record.Score));

                var projectedScores = collection.Query()
                    .Where(record => record.Score >= 2)
                    .OrderByDescending(record => record.Score)
                    .Select(record => record.Score)
                    .ToArray();
                CollectionAssert.AreEqual(new[] { 3, 2 }, projectedScores);

                var projectedEntities = collection.Query()
                    .Where(record => record.Score >= 2)
                    .Select(record => new GeneratedScalarRecord
                    {
                        Name = record.Name,
                        Score = record.Score + 1
                    })
                    .ToArray();
                Assert.AreEqual(2, projectedEntities.Length);
                CollectionAssert.AreEquivalent(new[] { 3, 4 }, projectedEntities.Select(record => record.Score).ToArray());

                var projectionException = Assert.ThrowsException<NotSupportedException>(() =>
                    collection.Query().Select(record => new { record.Name }).ToArray());
                StringAssert.Contains(projectionException.Message, "requires a registered generated execution map");

                var grouped = collection.Query()
                    .GroupBy(record => record.Score >= 2)
                    .ToList();
                Assert.AreEqual(2, grouped.Count);
                Assert.AreEqual(2, grouped.Single(group => group.Key).Count());

                var groupedKeys = collection.Query()
                    .GroupBy(record => record.Score >= 2)
                    .Select(group => group.Key)
                    .ToArray();
                CollectionAssert.AreEquivalent(new[] { false, true }, groupedKeys);

                Assert.AreEqual(2, collection.UpdateMany(
                    record => new GeneratedScalarRecord { Score = record.Score + 10 },
                    record => record.Score >= 2));
                Assert.AreEqual(2, collection.DeleteMany(record => record.Score >= 12));

                Assert.IsTrue(collection.EnsureIndex("score_parity", BsonExpression.Create("Score")));
                Assert.IsTrue(collection.DropIndex("score_parity"));
                Assert.AreEqual(1, collection.DeleteAll());

                var scalarCollection = database.GetGeneratedCollection<ScalarCompatibilityRecord>("generatedScalarProjection");
                scalarCollection.Insert(new ScalarCompatibilityRecord
                {
                    State = GeneratedScalarState.Completed,
                    NullableInteger = 42,
                    NullableState = GeneratedScalarState.Ready
                });
                Assert.AreEqual(
                    GeneratedScalarState.Completed,
                    scalarCollection.Query().Select(record => record.State).Single());
                Assert.AreEqual(
                    42,
                    scalarCollection.Query().Select(record => record.NullableInteger).Single());
                Assert.AreEqual(
                    GeneratedScalarState.Ready,
                    scalarCollection.Query().Select(record => record.NullableState).Single());

                var includeException = Assert.ThrowsException<NotSupportedException>(() =>
                    collection.Query().Include(BsonExpression.Create("$.Reference")));
                StringAssert.Contains(includeException.Message, "DbRef");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_MinAndMax_ReturnNullOrDefaultForEmptyCollection()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<GeneratedScalarRecord>("generatedEmptyAggregates");

                Assert.IsTrue(collection.Min(BsonExpression.Create("Score")).IsNull);
                Assert.IsTrue(collection.Max(BsonExpression.Create("Score")).IsNull);
                Assert.AreEqual(default, collection.Min(record => record.Score));
                Assert.AreEqual(default, collection.Max(record => record.Score));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_Query_UsesGeneratedDeserializer()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<GeneratedScalarRecord>("generatedQuery");
                collection.Insert(new[]
                {
                    new GeneratedScalarRecord { Name = "first", Score = 1 },
                    new GeneratedScalarRecord { Name = "second", Score = 2 },
                    new GeneratedScalarRecord { Name = "third", Score = 3 }
                });

                var records = collection.Query()
                    .Where(BsonExpression.Create("Score >= 2"))
                    .OrderByDescending(BsonExpression.Create("Score"))
                    .ToList();

                Assert.AreEqual(2, records.Count);
                Assert.AreEqual("third", records[0].Name);
                Assert.AreEqual("second", records[1].Name);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_EnsuresIndexesFromGeneratedMapping()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<GeneratedScalarRecord>("generatedIndexes");

                Assert.IsTrue(collection.EnsureIndex(record => record.Name));
                Assert.IsFalse(collection.EnsureIndex(record => record.Name));
                Assert.IsTrue(collection.EnsureIndex("score_idx", record => record.Score));
                Assert.IsTrue(collection.EnsureIndex(BsonExpression.Create("LOWER($.Name)")));
                Assert.IsTrue(collection.EnsureIndex("score_plus_one", BsonExpression.Create("$.Score + 1")));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_GeneratedScalarMap_ExecutesUpsertsWithoutMapperFallback()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<GeneratedScalarRecord>("generatedScalarUpserts");

                var automatic = new GeneratedScalarRecord { Name = "automatic", Score = 1 };
                Assert.IsTrue(collection.Upsert(automatic));
                Assert.AreNotEqual(0, automatic.Id);
                automatic.Name = "automatic-updated";
                automatic.Score = 2;
                Assert.IsFalse(collection.Upsert(automatic));
                Assert.AreEqual(1, collection.Count());
                Assert.AreEqual("automatic-updated", collection.FindById(automatic.Id)?.Name);
                Assert.AreEqual(2, collection.FindById(automatic.Id)?.Score);

                var batch = new[]
                {
                    new GeneratedScalarRecord { Name = "batch-first", Score = 3 },
                    new GeneratedScalarRecord { Name = "batch-second", Score = 4 }
                };
                Assert.AreEqual(2, collection.Upsert(batch));
                Assert.AreNotEqual(0, batch[0].Id);
                Assert.AreNotEqual(0, batch[1].Id);
                Assert.AreNotEqual(batch[0].Id, batch[1].Id);
                batch[0].Score = 30;
                batch[1].Score = 40;
                Assert.AreEqual(0, collection.Upsert(batch));
                Assert.AreEqual(30, collection.FindById(batch[0].Id)?.Score);
                Assert.AreEqual(40, collection.FindById(batch[1].Id)?.Score);

                var explicitEntity = new GeneratedScalarRecord { Id = 900, Name = "explicit", Score = 5 };
                Assert.IsTrue(collection.Upsert(41, explicitEntity));
                Assert.AreEqual(900, explicitEntity.Id);
                Assert.AreEqual("explicit", collection.FindById(41)?.Name);
                explicitEntity.Name = "explicit-updated";
                explicitEntity.Score = 50;
                Assert.IsFalse(collection.Upsert(41, explicitEntity));
                Assert.AreEqual(900, explicitEntity.Id);
                Assert.AreEqual("explicit-updated", collection.FindById(41)?.Name);
                Assert.AreEqual(50, collection.FindById(41)?.Score);
                Assert.AreEqual(4, collection.Count());
            }
            finally
            {
                File.Delete(path);
            }
        }

    }
}
