using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CompactVectorBounds_Tests
    {
        [Theory]
        [InlineData(null, 0)]
        [InlineData(null, 1)]
        [InlineData(null, 2)]
        [InlineData("password", 0)]
        [InlineData("password", 1)]
        [InlineData("password", 2)]
        public void Oversized_vector_rejects_transaction_and_preserves_committed_documents(string password, int nesting)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Password = password };
            var committed = Document(1, 3, nesting);
            using (var db = new LiteDatabase(connection))
            {
                var docs = db.GetCollection("docs");
                docs.EnsureIndex("Marker");
                docs.Insert(committed);
                db.Checkpoint();
                db.BeginTrans();
                docs.Insert(Document(2, 3, nesting));
                Action insert = () => docs.Insert(Document(3, ushort.MaxValue + 1, nesting));
                insert.Should().Throw<LiteException>().WithMessage("Vector length must fit into UInt16");
            }

            // The BSON vector validation closes the engine. Reopen must discard the
            // complete failed transaction, including its earlier valid document/schema.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var db = new LiteDatabase(connection);
                var docs = db.GetCollection("docs");
                docs.FindAll().Select(doc => doc["_id"].AsInt32).Should().Equal(1);
                BsonSerializer.Serialize(docs.FindById(1)).Should().Equal(BsonSerializer.Serialize(committed));
                docs.Find(Query.EQ("Marker", 1)).Select(doc => doc["_id"].AsInt32).Should().Equal(1);
                docs.Find(Query.EQ("Marker", 2)).Should().BeEmpty();
                db.Checkpoint();
            }
        }

        [Theory]
        [InlineData(null, 0)]
        [InlineData(null, 1)]
        [InlineData(null, 2)]
        [InlineData("password", 0)]
        [InlineData("password", 1)]
        [InlineData("password", 2)]
        public void Maximum_vector_dimension_round_trips_compact_storage(string password, int nesting)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Password = password };
            var expected = Document(1, ushort.MaxValue, nesting);
            using (var db = new LiteDatabase(connection)) db.GetCollection("docs").Insert(expected);
            using (var db = new LiteDatabase(connection))
            {
                var actual = db.GetCollection("docs").FindById(1);
                BsonSerializer.Serialize(actual).Should().Equal(BsonSerializer.Serialize(expected));
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Codec_uses_compact_encoding_at_supported_vector_boundary(int nesting)
        {
            var expected = Document(1, ushort.MaxValue, nesting);
            var catalog = new SchemaCatalog();
            var payload = CompactCodec_Tests.Encode(expected, catalog);
            payload.Length.Should().BeLessThan(expected.GetBytesCount(true) - 8);
            using var reader = new BufferReader(payload);
            var actual = DocumentStorageCodec.Read(reader, catalog: () => catalog).GetValue();
            BsonSerializer.Serialize(actual).Should().Equal(BsonSerializer.Serialize(expected));
        }

        private static BsonDocument Document(int id, int dimensions, int nesting)
        {
            var vector = new BsonVector(Enumerable.Range(0, dimensions).Select(i => (float)(i % 101 - 50)).ToArray());
            return new BsonDocument
            {
                ["_id"] = id,
                ["Marker"] = id,
                ["Vector"] = nesting == 0 ? (BsonValue)vector : nesting == 1 ?
                    new BsonDocument { ["Embedding"] = vector } : (BsonValue)new BsonArray { vector },
                // Array-key removal makes compact encoding beneficial even with
                // a large vector and no previously admitted schema.
                ["Values"] = new BsonArray(Enumerable.Range(0, 200).Select(i => new BsonValue(i)))
            };
        }
    }
}
