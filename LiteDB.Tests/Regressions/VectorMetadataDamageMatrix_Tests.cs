using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;
using static LiteDB.Tests.Regressions.VectorMetadataDamageFiles;

namespace LiteDB.Tests.Regressions
{
    public class VectorMetadataDamageMatrix_Tests
    {
        public static IEnumerable<object[]> DamageCases()
        {
            foreach (var kind in new[] { "malformed", "incomplete", "duplicated", "slot", "dimension", "metric" })
            foreach (var password in new[] { null, "vector-secret" })
            foreach (var state in new[] { "current", "migrated", "legacy" })
            foreach (var operation in new[] { "query", "insert", "update", "delete", "ensure" })
                yield return new object[] { kind, password, state, operation };
        }

        [Theory]
        [MemberData(nameof(DamageCases))]
        public void Damaged_metadata_cannot_admit_queries_or_writes(string kind, string password, string state, string operation)
        {
            using var file = new TempFile();
            Create(file.Filename, password, state, VectorDistanceMetric.Euclidean);
            if (state != "legacy")
            {
                using var healthy = new LiteDatabase(Connection(file.Filename, password));
                var query = healthy.GetCollection("docs").Query().TopKNear("Embedding", new[] { 1f, 0f }, 1);
                query.GetPlan()["index"]["name"].AsString.Should().Be("embedding");
                query.ToArray().Single()["_id"].AsInt32.Should().Be(2,
                    "Euclidean chooses row 2 whereas a cosine fallback chooses row 1");
            }
            Damage(file.Filename, password, kind);
            var data = File.ReadAllBytes(file.Filename);
            var log = LogBytes(file.Filename);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                Action access = () =>
                {
                    using var db = new LiteDatabase(Connection(file.Filename, password));
                    Operate(db.GetCollection("docs"), operation);
                };
                var error = access.Should().Throw<LiteException>().Which;
                error.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
                if (state == "legacy")
                {
                    error.Message.Should().Contain("'embedding' has no vector metadata");
                }
                else
                {
                    AssertDiagnostic(error);
                    File.ReadAllBytes(file.Filename).Should().Equal(data, "refusal and disposal preserve the data on every retry");
                    LogBytes(file.Filename).Should().Equal(log, "refusal and disposal preserve the recovery WAL");
                    using var inspect = new LiteDatabase(Connection(file.Filename, password));
                    inspect.GetCollection("docs").FindAll().Select(x => x["_id"].AsInt32)
                        .Should().BeEquivalentTo(new[] { 1, 2 });
                    inspect.GetCollection("unrelated").FindById(7)["value"].AsString.Should().Be("preserve me");
                }
            }
        }

        public static IEnumerable<object[]> HealthyCases()
        {
            foreach (var state in new[] { "current", "migrated" })
            foreach (var password in new[] { null, "vector-secret" })
            foreach (var metric in new[] { VectorDistanceMetric.Euclidean, VectorDistanceMetric.Cosine, VectorDistanceMetric.DotProduct })
                yield return new object[] { state, password, metric };
        }

        [Theory]
        [MemberData(nameof(HealthyCases))]
        public void Healthy_indexes_keep_their_metric_and_maintain_writes(string state, string password, VectorDistanceMetric metric)
        {
            using var file = new TempFile();
            Create(file.Filename, password, state, metric);
            var expected = metric == VectorDistanceMetric.Euclidean ? 2 : 1;
            using (var db = new LiteDatabase(Connection(file.Filename, password)))
            {
                AssertNearest(db, new[] { 1f, 0f }, expected);
                var docs = db.GetCollection("docs");
                docs.Insert(VectorDocument(3, 0f, 10f));
                AssertNearest(db, new[] { 0f, 10f }, 3);
                docs.Update(VectorDocument(3, -10f, 0f)).Should().BeTrue();
                AssertNearest(db, new[] { -10f, 0f }, 3);
                docs.Delete(3).Should().BeTrue();
                AssertNearest(db, new[] { 1f, 0f }, expected);
            }
            using var reopened = new LiteDatabase(Connection(file.Filename, password));
            AssertNearest(reopened, new[] { 1f, 0f }, expected);
            reopened.GetCollection("docs").Count().Should().Be(2);
            reopened.GetCollection("unrelated").FindById(7)["value"].AsString.Should().Be("preserve me");
        }

        public static IEnumerable<object[]> LegacyCases() => DamageCases()
            .Where(x => (string)x[2] == "legacy" && (string)x[3] == "query")
            .Select(x => new[] { x[0], x[1] });

        [Theory]
        [MemberData(nameof(LegacyCases))]
        public void Legacy_corrupt_metadata_still_allows_read_only_document_inspection(string kind, string password)
        {
            using var file = new TempFile();
            LegacyFixture(file.Filename, password);
            Damage(file.Filename, password, kind);
            var before = File.ReadAllBytes(file.Filename);
            var log = LogBytes(file.Filename);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using (var db = new LiteDatabase(Connection(file.Filename, password) + ";readonly=true;legacy index scan=true"))
                {
                    db.GetCollection("docs").FindAll().Select(x => x["_id"].AsInt32)
                        .Should().BeEquivalentTo(Enumerable.Range(1, 40));
                    db.GetCollection("computed").Count().Should().Be(20);
                }
                File.ReadAllBytes(file.Filename).Should().Equal(before);
                LogBytes(file.Filename).Should().Equal(log);
            }
        }

        public static IEnumerable<object[]> RecoveryCases() => DamageCases()
            .Where(x => (string)x[3] == "query").Select(x => new[] { x[0], x[1], x[2] });

        [Theory]
        [MemberData(nameof(RecoveryCases))]
        public void Rebuild_reports_only_the_damaged_index_and_preserves_documents(string kind, string password, string state)
        {
            using var file = new TempFile();
            Create(file.Filename, password, state, VectorDistanceMetric.Euclidean);
            BsonDocument[] expectedDocuments;
            BsonDocument[] expectedComputed;
            using (var original = new LiteDatabase(Connection(file.Filename, password) + ";readonly=true;legacy index scan=true"))
            {
                expectedDocuments = original.GetCollection("docs").FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
                expectedComputed = original.GetCollection("computed").FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
            }
            Damage(file.Filename, password, kind);
            var expectedIds = state == "legacy" ? Enumerable.Range(1, 40).ToArray() : new[] { 1, 2 };
            if (state == "legacy")
            {
                // Legacy migration reports the missing metadata and marks the file for rebuild.
                Action open = () => new LiteDatabase(Connection(file.Filename, password)).Dispose();
                open.Should().Throw<LiteException>().Where(x => x.ErrorCode == LiteException.INVALID_DATAFILE_STATE)
                    .WithMessage("*'embedding' has no vector metadata*");
            }
            using (var rebuilt = new LiteDatabase(Connection(file.Filename, password) + ";Auto-Rebuild=true"))
            {
                if (state != "legacy")
                {
                    Action query = () => Operate(rebuilt.GetCollection("docs"), "query");
                    AssertDiagnostic(query.Should().Throw<LiteException>().Which);
                    // DropIndex also requires the rejected write snapshot: only Rebuild is usable.
                    Action drop = () => rebuilt.GetCollection("docs").DropIndex("embedding");
                    AssertDiagnostic(drop.Should().Throw<LiteException>().Which);
                    rebuilt.Rebuild();
                }
                var docs = rebuilt.GetCollection("docs");
                docs.FindAll().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(expectedIds);
                docs.Count().Should().Be(expectedIds.Length);
                docs.FindAll().OrderBy(x => x["_id"].AsInt32).Should().Equal(expectedDocuments);
                rebuilt.GetCollection("$indexes").Find(Query.EQ("collection", "docs"))
                    .Select(x => x["name"].AsString).Should().BeEquivalentTo(
                        state == "current" ? new[] { "_id" } : new[] { "_id", "name" });
                rebuilt.GetCollection("_rebuild_errors").FindAll().Select(x => x["message"].AsString)
                    .Should().ContainSingle().Which.Should().Contain("embedding").And.NotContain("coalesced");
                if (state != "current")
                {
                    VectorMetadataRebuildAssertions.ComputedIndex(rebuilt, file.Filename, password, expectedComputed);
                }
                if (state != "legacy")
                {
                    rebuilt.GetCollection("unrelated").FindById(7)["value"].AsString.Should().Be("preserve me");
                    docs.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));
                    AssertNearest(rebuilt, new[] { 1f, 0f }, 2);
                }
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var db = new LiteDatabase(Connection(file.Filename, password));
                db.GetCollection("docs").Count().Should().Be(expectedIds.Length + attempt);
                db.GetCollection("docs").FindAll().Where(x => x["_id"].AsInt32 < 100)
                    .OrderBy(x => x["_id"].AsInt32).Should().Equal(expectedDocuments);
                if (state != "current")
                {
                    VectorMetadataRebuildAssertions.ComputedIndex(db, file.Filename, password, expectedComputed);
                }
                db.GetCollection("docs").Insert(VectorDocument(100 + attempt, 1f, 2f));
            }
        }

        private static void Operate(ILiteCollection<BsonDocument> docs, string operation)
        {
            switch (operation)
            {
                case "query": docs.Query().TopKNear("Embedding", new[] { 1f, 0f }, 1).ToArray(); break;
                case "insert": docs.Insert(VectorDocument(3, 1f, 2f)); break;
                case "update": docs.Update(VectorDocument(1, 1f, 2f)); break;
                case "delete": docs.Delete(1); break;
                case "ensure": docs.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2, VectorDistanceMetric.Euclidean)); break;
                default: throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        private static void AssertNearest(LiteDatabase db, float[] target, int expected)
        {
            var query = db.GetCollection("docs").Query().TopKNear("Embedding", target, 1);
            query.GetPlan()["index"]["name"].AsString.Should().Be("embedding");
            query.ToArray().Single()["_id"].AsInt32.Should().Be(expected);
        }

        private static void AssertDiagnostic(LiteException error)
        {
            error.Message.Should().Contain("docs").And.Contain("embedding")
                .And.Contain("Rebuild");
        }
    }
}
