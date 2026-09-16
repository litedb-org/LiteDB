using System.Collections.Generic;
using System.Linq;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2033CustomProjection_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public float[] Embedding { get; set; }
            public byte[] Bytes { get; set; }
            public List<int> Values { get; set; }
        }

        private sealed class ProjectionMapper : BsonMapper
        {
            public int Calls { get; private set; }
            public override T ToObject<T>(BsonDocument doc)
            {
                if (typeof(T) == typeof(List<int>))
                {
                    Calls++;
                    var values = (List<int>)(object)base.ToObject<T>(doc);
                    values.Add(99);
                    return (T)(object)values;
                }
                return base.ToObject<T>(doc);
            }
        }

        [Fact]
        public void Typed_array_projection_still_invokes_virtual_ToObject_hook()
        {
            var mapper = new ProjectionMapper();
            using var db = new LiteDatabase(":memory:", mapper);
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new Row { Id = 1, Values = new List<int> { 7 }, Embedding = new[] { 1f, 0f } });
            Assert.Equal(new[] { 7, 99 }, rows.Query().Select(x => x.Values).Single());
            Assert.Equal(1, mapper.Calls);
            var scored = rows.Query().WhereNear(x => x.Embedding, new[] { 1f, 0f }, .1)
                .Select(x => x.Values).WithScore().Single();
            Assert.Equal(new[] { 7, 99 }, scored.Document);
            Assert.Equal(2, mapper.Calls);
        }

        [Fact]
        public void Custom_document_serialized_collection_is_not_unwrapped()
        {
            var mapper = new BsonMapper();
            mapper.RegisterType<List<int>>(values => new BsonDocument { ["only"] = new BsonArray(values.Select(x => new BsonValue(x))) },
                value => value["only"].AsArray.Select(x => x.AsInt32).ToList());
            using var db = new LiteDatabase(":memory:", mapper);
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new Row { Id = 1, Values = new List<int> { 4, 4 } });
            Assert.Equal(new[] { 4, 4 }, rows.Query().Select(x => x.Values).Single());
        }

        [Fact]
        public void Read_transform_document_replacement_preserves_array_projection_shape()
        {
            using var db = new LiteDatabase(new LiteEngine(new EngineSettings
            {
                Filename = ":memory:",
                ReadTransform = (_, value) => value.IsDocument ? new BsonDocument(value.AsDocument.RawValue) : value
            }));
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new Row { Id = 1, Values = new List<int> { 3, 2, 3 } });
            rows.Insert(new Row { Id = 2, Values = new List<int>() });
            var values = rows.Query().OrderBy(x => x.Id).Select(x => x.Values).ToArray();
            Assert.Equal(new[] { 3, 2, 3 }, values[0]);
            Assert.Empty(values[1]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Scored_vector_projections_unwrap_collections_after_transform(bool customDocument)
        {
            var mapper = new BsonMapper();
            if (customDocument)
                mapper.RegisterType<List<int>>(values => new BsonDocument { ["only"] = new BsonArray(values.Select(x => new BsonValue(x))) },
                    value => value["only"].AsArray.Select(x => x.AsInt32).ToList());
            using var db = new LiteDatabase(new LiteEngine(new EngineSettings
            {
                Filename = ":memory:",
                ReadTransform = (_, value) => BsonSerializer.Deserialize(BsonSerializer.Serialize(value.AsDocument))
            }), mapper);
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new Row { Id = 1, Values = new List<int> { 8, 8 }, Embedding = new[] { 1f, 0f } });
            rows.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2));
            var result = rows.Query().WhereNear(x => x.Embedding, new[] { 1f, 0f }, .1)
                .Select(x => x.Values).WithScore().Single();
            Assert.Equal(new[] { 8, 8 }, result.Document);
            Assert.Equal(0d, result.Score);
        }

        [Fact]
        public void Custom_document_serialized_bytes_keep_document_shape_in_both_terminals()
        {
            var mapper = new BsonMapper();
            mapper.RegisterType<byte[]>(bytes => new BsonDocument { ["data"] = new BsonArray(bytes.Select(x => new BsonValue((int)x))) },
                value => value["data"].AsArray.Select(x => (byte)x.AsInt32).ToArray());
            using var db = new LiteDatabase(":memory:", mapper);
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new Row { Id = 1, Bytes = new byte[] { 5, 6 }, Embedding = new[] { 1f, 0f } });
            Assert.Equal(new byte[] { 5, 6 }, rows.Query().Select(x => x.Bytes).Single());
            var scored = rows.Query().WhereNear(x => x.Embedding, new[] { 1f, 0f }, .1)
                .Select(x => x.Bytes).WithScore().Single();
            Assert.Equal(new byte[] { 5, 6 }, scored.Document);
        }

        [Fact]
        public void Grouped_array_projection_keeps_one_collection_per_group()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new Row { Id = 1 });
            rows.Insert(new Row { Id = 2 });
            var result = rows.Query().GroupBy(x => x.Id).Select(group => group.Select(row => row.Id).ToArray()).ToArray();
            Assert.Equal(2, result.Length);
            Assert.Equal(new[] { 1 }, result[0]);
            Assert.Equal(new[] { 2 }, result[1]);
        }
    }
}
