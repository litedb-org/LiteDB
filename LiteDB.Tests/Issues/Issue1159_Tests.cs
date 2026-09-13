
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1159_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public decimal Coefficient { get; set; }
            public decimal? Sum { get; set; }
            public decimal? AdjustedSum => Sum * Coefficient;
        }

        [Fact]
        public void Repeated_ignore_is_idempotent_without_dropping_other_fields()
        {
            var mapper = new BsonMapper();
            mapper.Entity<Row>().Ignore(x => x.AdjustedSum);
            mapper.Entity<Row>().Ignore(x => x.AdjustedSum);
            using var db = new LiteDatabase(":memory:", mapper);
            db.GetCollection<Row>("rows").Insert(new Row { Id = 4, Coefficient = 3, Sum = 11 });
            var raw = db.GetCollection("rows").FindById(4);
            raw.ContainsKey("AdjustedSum").Should().BeFalse();
            raw["Coefficient"].AsDecimal.Should().Be(3);
            raw["Sum"].AsDecimal.Should().Be(11);
            db.GetCollection<Row>("rows").FindById(4).AdjustedSum.Should().Be(33);
        }
    }
}
