using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests
{
    /// <summary>
    /// Reusable compatibility vector for the ordinary mapper and its generated counterpart.
    /// The two writers intentionally use different database files, so raw evidence cannot be overwritten.
    /// </summary>
    internal sealed class BsonCompatibilityVector<TOrdinary, TGenerated>
    {
        public required Func<TOrdinary> CreateOrdinary { get; init; }
        public required Func<TGenerated> CreateGenerated { get; init; }
        public required Func<BsonMapper> CreateOrdinaryMapper { get; init; }
        public required Func<BsonMapper> CreateGeneratedMapper { get; init; }
        public required BsonDocument Golden { get; init; }
        public required Action<TOrdinary, BsonValue> VerifyOrdinary { get; init; }
        public required Action<TGenerated, BsonValue> VerifyGenerated { get; init; }
        public string CollectionName { get; init; } = "compatibility";

        public void Execute()
        {
            var ordinaryPath = Path.Combine(Path.GetTempPath(), $"litedb-ordinary-{Guid.NewGuid():N}.db");
            var generatedPath = Path.Combine(Path.GetTempPath(), $"litedb-generated-{Guid.NewGuid():N}.db");
            var ordinaryEntity = CreateOrdinary();
            var generatedEntity = CreateGenerated();
            BsonValue ordinaryId;
            BsonValue generatedId;

            try
            {
                using (var database = new LiteDatabase(ordinaryPath, CreateOrdinaryMapper()))
                {
                    ordinaryId = database.GetCollection<TOrdinary>(CollectionName).Insert(ordinaryEntity);
                }

                using (var database = new LiteDatabase(generatedPath, CreateGeneratedMapper()))
                {
                    generatedId = database.GetGeneratedCollection<TGenerated>(CollectionName).Insert(generatedEntity);
                }

                VerifyOrdinary(ordinaryEntity, ordinaryId);
                VerifyGenerated(generatedEntity, generatedId);

                var ordinaryDocument = ReadRaw(ordinaryPath, ordinaryId);
                var generatedDocument = ReadRaw(generatedPath, generatedId);
                BsonCompatibilityAssert.AreEqual(Golden, ordinaryDocument);
                BsonCompatibilityAssert.AreEqual(Golden, generatedDocument);
                BsonCompatibilityAssert.AreEqual(ordinaryDocument, generatedDocument);

                using (var database = new LiteDatabase(ordinaryPath, CreateGeneratedMapper()))
                {
                    var crossRead = database.GetGeneratedCollection<TGenerated>(CollectionName).FindById(ordinaryId);
                    Assert.IsNotNull(crossRead);
                    VerifyGenerated(crossRead, ordinaryId);
                }

                using (var database = new LiteDatabase(generatedPath, CreateOrdinaryMapper()))
                {
                    var crossRead = database.GetCollection<TOrdinary>(CollectionName).FindById(generatedId);
                    Assert.IsNotNull(crossRead);
                    VerifyOrdinary(crossRead, generatedId);
                }
            }
            finally
            {
                File.Delete(ordinaryPath);
                File.Delete(generatedPath);
            }
        }

        private BsonDocument ReadRaw(string path, BsonValue id)
        {
            using var database = new LiteDatabase(path);
            return database.GetCollection(CollectionName).FindById(id);
        }
    }
}
