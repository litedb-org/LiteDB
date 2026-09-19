using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2770_Tests
    {
        public enum Kind { First, Second }
        public class Row
        {
            public int Id { get; set; }
            public Kind Type { get; set; }
            public Kind BackupType { get; set; }
        }

        [Fact]
        public void Closed_nested_lambda_remains_a_constant_enum_operand()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper { EnumAsInteger = false });
            var col = db.GetCollection<Row>();
            col.Insert(new[] { new Row { Id = 1, Type = Kind.First }, new Row { Id = 2, Type = Kind.Second } });
            col.Find(x => x.Type == new[] { Kind.First }.Select(kind => kind).First())
                .Select(x => x.Id).Should().Equal(1);
        }

        [Fact]
        public void Numeric_enum_operations_do_not_compare_or_add_stored_names()
        {
            var mapper = new BsonMapper { EnumAsInteger = false };
            Action order = () => mapper.GetExpression<Row, bool>(x => (int)x.Type < (int)x.BackupType);
            Action add = () => mapper.GetExpression<Row, int>(x => (int)x.Type + (int)x.BackupType);
            order.Should().Throw<InvalidOperationException>();
            add.Should().Throw<InvalidOperationException>();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Enum_property_comparison_matches_CLR_for_equal_and_unequal_rows(bool asInteger)
        {
            var rows = new[]
            {
                new Row { Id = 1, Type = Kind.First, BackupType = Kind.First },
                new Row { Id = 2, Type = Kind.First, BackupType = Kind.Second },
                new Row { Id = 3, Type = Kind.Second, BackupType = Kind.First },
                new Row { Id = 4, Type = Kind.Second, BackupType = Kind.Second }
            };
            using var db = new LiteDatabase(":memory:", new BsonMapper { EnumAsInteger = asInteger });
            var col = db.GetCollection<Row>();
            col.Insert(rows);
            col.Find(x => x.Type == Kind.First).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 2);
            col.Find(x => x.Type == x.BackupType).Select(x => x.Id).OrderBy(x => x)
                .Should().Equal(rows.Where(x => x.Type == x.BackupType).Select(x => x.Id));
            col.Find(x => x.Type != x.BackupType).Select(x => x.Id).OrderBy(x => x)
                .Should().Equal(2, 3);
            col.Count().Should().Be(4);
        }
    }
}
