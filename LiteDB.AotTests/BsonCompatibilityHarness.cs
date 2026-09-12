using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

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

    /// <summary>Structural, type-sensitive BSON assertions and deterministic fingerprints.</summary>
    internal static class BsonCompatibilityAssert
    {
        public static void AreEqual(BsonValue expected, BsonValue actual) => Compare(expected, actual, "$" );

        public static string Fingerprint(BsonDocument document)
        {
            var bytes = BsonSerializer.Serialize(Canonicalize(document).AsDocument);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        private static void Compare(BsonValue expected, BsonValue actual, string path)
        {
            Assert.AreEqual(expected.Type, actual.Type, $"BSON type differs at {path}.");

            if (expected.IsDocument)
            {
                var expectedDocument = expected.AsDocument;
                var actualDocument = actual.AsDocument;
                CollectionAssert.AreEquivalent(expectedDocument.Keys.ToArray(), actualDocument.Keys.ToArray(),
                    $"Fields differ at {path}; omitted and explicit null fields are distinct.");
                foreach (var key in expectedDocument.Keys)
                {
                    Assert.IsTrue(actualDocument.ContainsKey(key), $"Missing field {path}.{key}.");
                    Compare(expectedDocument[key], actualDocument[key], $"{path}.{key}");
                }
                return;
            }

            if (expected.IsArray)
            {
                Assert.AreEqual(expected.AsArray.Count, actual.AsArray.Count, $"Array length differs at {path}.");
                for (var index = 0; index < expected.AsArray.Count; index++)
                {
                    Compare(expected.AsArray[index], actual.AsArray[index], $"{path}[{index}]");
                }
                return;
            }

            if (expected.IsBinary)
            {
                CollectionAssert.AreEqual(expected.AsBinary, actual.AsBinary, $"Binary content differs at {path}.");
                return;
            }

            Assert.AreEqual(expected, actual, $"Typed BSON value differs at {path}.");
        }

        private static BsonValue Canonicalize(BsonValue value)
        {
            if (value.IsDocument)
            {
                var result = new BsonDocument();
                foreach (var key in value.AsDocument.Keys.OrderBy(key => key, StringComparer.Ordinal))
                {
                    result[key] = Canonicalize(value.AsDocument[key]);
                }
                return result;
            }

            if (value.IsArray)
            {
                return new BsonArray(value.AsArray.Select(Canonicalize));
            }

            return value;
        }
    }
}
