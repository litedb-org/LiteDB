using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CompactStorageSnapshot_Tests
    {
        [Fact]
        public void Old_reader_keeps_its_catalog_when_another_transaction_commits_a_new_shape()
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", CompactStorage = CompactStorageMode.Compact });
            var docs = db.GetCollection("docs");
            docs.Insert(Enumerable.Range(1, 20).Select(CompactStorage_Tests.Document));
            using var reader = docs.FindAll().GetEnumerator();
            reader.MoveNext().Should().BeTrue();
            var seen = 1;
#pragma warning disable xUnit1031 // The read snapshot is thread-affine; the competing writer must finish on another thread.
            Task.Run(() =>
            {
                docs.Insert(Enumerable.Range(21, 20).Select(i =>
                {
                    var doc = CompactStorage_Tests.Document(i);
                    doc["NewOptionalProperty"] = "new";
                    return doc;
                }));
            }).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            while (reader.MoveNext())
            {
                reader.Current.ContainsKey("NewOptionalProperty").Should().BeFalse();
                seen++;
            }
            seen.Should().Be(20);
            docs.FindAll().Count().Should().Be(40);
        }

        [Fact]
        public void Single_document_transactions_reuse_shapes_and_checkpoint_cannot_reuse_stale_catalog()
        {
            using var stream = new MemoryStream();
            using var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = stream, CompactStorage = CompactStorageMode.Compact }));
            var docs = db.GetCollection("docs");
            for (var i = 1; i <= 10; i++) docs.Insert(CompactStorage_Tests.Document(i));
            stream.ToArray()[59].Should().Be(10);
            docs.FindById(2)["RepeatedPropertyName0"].AsInt32.Should().Be(2);
            db.Checkpoint();
            db.DropCollection("docs");
            var replacement = db.GetCollection("replacement");
            for (var i = 1; i <= 10; i++)
            {
                var doc = new BsonDocument { ["_id"] = i };
                for (var f = 0; f < 15; f++) doc["DifferentPropertyName" + f] = i;
                replacement.Insert(doc);
            }
            replacement.FindById(2).ContainsKey("RepeatedPropertyName0").Should().BeFalse();
            replacement.FindById(2)["DifferentPropertyName0"].AsInt32.Should().Be(2);
        }

        [Fact]
        public void Catalog_budget_is_bounded_and_all_schema_pages_are_reclaimed_on_drop()
        {
            using var stream = new MemoryStream();
            using var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = stream, CompactStorage = CompactStorageMode.Compact, TransactionPageLimit = 4 }));
            var docs = Enumerable.Range(0, 600).Select(i =>
            {
                var doc = new BsonDocument { ["_id"] = i };
                for (var f = 0; f < 15; f++) doc[$"Shape{i / 2}LongField{f}"] = i;
                return doc;
            }).ToArray();
            db.GetCollection("docs").Insert(docs);
            db.GetCollection("docs").FindAll().Count().Should().Be(600);
            db.Checkpoint();
            var before = stream.ToArray();
            var schemaPages = Enumerable.Range(0, before.Length / 8192).Where(i => before[i * 8192 + 4] == 6).ToArray();
            schemaPages.Length.Should().BeGreaterThan(1).And.BeLessThanOrEqualTo(StorageSchema.MaxSchemas);
            db.DropCollection("docs");
            db.Checkpoint();
            var after = stream.ToArray();
            foreach (var page in schemaPages) after[page * 8192 + 4].Should().Be(0);
        }

        [Fact]
        public void Shared_connections_read_compact_data_and_can_keep_writing_legacy_documents()
        {
            using var file = new TempFile();
            using var compact = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared, CompactStorage = CompactStorageMode.Compact });
            using var legacyWrites = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared, CompactStorage = CompactStorageMode.Legacy });
            compact.GetCollection("docs").Insert(Enumerable.Range(1, 10).Select(CompactStorage_Tests.Document));
            legacyWrites.GetCollection("docs").FindById(2)["RepeatedPropertyName0"].AsInt32.Should().Be(2);
            legacyWrites.GetCollection("docs").Insert(CompactStorage_Tests.Document(11));
            compact.GetCollection("docs").FindAll().Count().Should().Be(11);
        }
    }
}
