#if NET8_0_OR_GREATER
using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1929_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public Index Selection { get; set; }
            public string Name { get; set; }
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(3, false)]
        [InlineData(1, true)]
        [InlineData(3, true)]
        public void Index_struct_preserves_value_direction_and_offset_after_reopen(int value, bool fromEnd)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                db.GetCollection<Row>("rows").Insert(new Row { Id = 1, Name = "sentinel", Selection = new Index(value, fromEnd) });
            }
            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                var row = db.GetCollection<Row>("rows").FindById(1);
                row.Selection.Value.Should().Be(value);
                row.Selection.IsFromEnd.Should().Be(fromEnd);
                row.Selection.GetOffset(10).Should().Be(fromEnd ? 10 - value : value);
                row.Name.Should().Be("sentinel");
                var raw = db.GetCollection("rows").FindById(1)["Selection"];
                raw["Value"].AsInt32.Should().Be(value);
                raw["IsFromEnd"].AsBoolean.Should().Be(fromEnd);
            }
        }
    }
}
#endif
