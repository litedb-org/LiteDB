using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2770MixedNumeric_Tests
    {
        public enum Kind
        {
            Zero = 0,
            One = 1,
            Two = 2
        }

        public class Row
        {
            public int Id { get; set; }

            public Kind Type { get; set; }

            public Kind Other { get; set; }
        }

        private static ILiteCollection<Row> Seed(LiteDatabase db)
        {
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new Row { Id = 1, Type = Kind.One, Other = Kind.One });
            rows.Insert(new Row { Id = 2, Type = Kind.One, Other = Kind.Two });

            return rows;
        }

        [Fact]
        public void Name_stored_enum_compared_with_a_numeric_field_is_rejected_instead_of_matching_nothing()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var rows = Seed(db);

            // the stored value is "One", the other side is the number 1: no BSON comparison can honour CLR semantics
            Action equal = () => rows.Find(x => (int)x.Type == x.Id).ToArray();
            Action notEqual = () => rows.Find(x => (int)x.Type != x.Id).ToArray();

            equal.Should().Throw<NotSupportedException>();
            notEqual.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Numeric_field_cast_to_a_name_stored_enum_is_rejected_instead_of_matching_nothing()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var rows = Seed(db);

            // (Kind)x.Id is still the number 1 once the cast is dropped, while the stored value is "One"
            Action equal = () => rows.Find(x => x.Type == (Kind)x.Id).ToArray();
            Action notEqual = () => rows.Find(x => x.Type != (Kind)x.Id).ToArray();
            Action reversed = () => rows.Find(x => (Kind)x.Id == x.Type).ToArray();

            equal.Should().Throw<NotSupportedException>();
            notEqual.Should().Throw<NotSupportedException>();
            reversed.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Numeric_field_cast_to_an_integer_stored_enum_keeps_clr_semantics()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper { EnumAsInteger = true });
            var rows = Seed(db);

            rows.Find(x => x.Type == (Kind)x.Id).Select(x => x.Id).Should().Equal(1);
        }

        [Fact]
        public void Numeric_field_cast_to_an_enum_and_compared_with_a_constant_stays_numeric()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var rows = Seed(db);

            // the row value is the number 1, so the constant must not be rewritten to the name "One"
            rows.Find(x => (Kind)x.Id == Kind.One).Select(x => x.Id).Should().Equal(1);
            rows.Find(x => x.Type == Kind.One).Select(x => x.Id).Should().Equal(1, 2);
        }

        [Fact]
        public void Name_stored_enum_compared_with_a_field_of_the_same_enum_still_translates()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var rows = Seed(db);

            rows.Find(x => x.Type == x.Other).Select(x => x.Id).Should().Equal(1);
            rows.Find(x => x.Type != x.Other).Select(x => x.Id).Should().Equal(2);
        }

        [Fact]
        public void Integer_stored_enum_compared_with_a_numeric_field_keeps_clr_semantics()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper { EnumAsInteger = true });
            var rows = Seed(db);

            rows.Find(x => (int)x.Type == x.Id).Select(x => x.Id).Should().Equal(1);
        }
    }
}
