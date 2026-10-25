using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2033Projection_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public byte[] Bytes { get; set; }
            public int[] Values { get; set; }
            public Dictionary<string, int[]> Dictionary { get; set; }
            public BsonArray Array { get; set; }
        }

        [Fact]
        public void Arrays_preserve_null_empty_binary_and_bson_array_shapes()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new Row { Id = 1, Bytes = new byte[] { 1, 2 }, Values = new[] { 3, 3 }, Array = new BsonArray(4, 4) });
            rows.Insert(new Row { Id = 2, Bytes = new byte[0], Values = new int[0], Array = new BsonArray() });
            rows.Insert(new Row { Id = 3 });
            var values = rows.Query().OrderBy(x => x.Id).Select(x => x.Values).ToArray();
            Assert.Equal(new[] { 3, 3 }, values[0]);
            Assert.Empty(values[1]);
            Assert.Null(values[2]);
            var bytes = rows.Query().OrderBy(x => x.Id).Select(x => x.Bytes).ToArray();
            Assert.Equal(new byte[] { 1, 2 }, bytes[0]);
            Assert.Empty(bytes[1]);
            Assert.Null(bytes[2]);
            var arrays = rows.Query().OrderBy(x => x.Id).Select(x => x.Array).ToArray();
            Assert.Equal(new[] { 4, 4 }, arrays[0].Select(x => x.AsInt32));
            Assert.Empty((IEnumerable<BsonValue>)arrays[1]);
            Assert.Null(arrays[2]);
        }

        [Fact]
        public void Dictionary_projection_keeps_its_document_shape()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new Row { Id = 1, Dictionary = new Dictionary<string, int[]> { ["only"] = new[] { 1, 2 } } });
            var result = rows.Query().Select(x => x.Dictionary).Single();
            Assert.Equal(new[] { 1, 2 }, result["only"]);
        }
    }
}
