using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CompactStorageAdmission_Tests
    {
        [Fact]
        public void Updating_one_off_shapes_does_not_create_catalogs()
        {
            using var stream = new MemoryStream();
            using var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = stream, CompactStorage = true }));
            var docs = Enumerable.Range(1, 1000).Select(id =>
            {
                var doc = new BsonDocument { ["_id"] = id };
                for (var f = 0; f < 15; f++) doc[$"Unique_{id}_{f}"] = id;
                return doc;
            }).ToArray();
            db.GetCollection("docs").Insert(docs);
            db.GetCollection("docs").Update(docs);
            db.Checkpoint();
            stream.ToArray()[59].Should().Be(8);
            db.GetCollection("docs").FindAll().Count().Should().Be(1000);
        }

        [Fact]
        public void Oversized_logical_bson_remains_invalid_even_with_compact_enabled()
        {
            using var stream = new MemoryStream();
            using var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = stream, CompactStorage = true }));
            Action insert = () => db.GetCollection("docs").Insert(new BsonDocument
            {
                ["_id"] = 1, ["payload"] = new byte[Constants.MAX_DOCUMENT_SIZE]
            });
            insert.Should().Throw<LiteException>().WithMessage("Document size exceed*");
            stream.ToArray()[59].Should().Be(8);
        }

        [Fact]
        public void Malformed_unicode_is_rejected_just_as_in_public_bson()
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", CompactStorage = true });
            var doc = CompactStorage_Tests.Document(1);
            doc["Text"] = "unpaired\ud800";
            Action serialize = () => BsonSerializer.Serialize(doc);
            Action insert = () => db.GetCollection("docs").Insert(doc);
            serialize.Should().Throw<System.Text.EncoderFallbackException>();
            insert.Should().Throw<System.Text.EncoderFallbackException>();
        }
    }
}
