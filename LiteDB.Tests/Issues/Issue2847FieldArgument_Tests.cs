using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2847FieldArgument_Tests
    {
        public class Row
        {
            public int Id { get; set; }

            public string Name { get; set; }

            public string Part { get; set; }
        }

        // The compared text comes from the row: only row 1 holds a usable string there.
        private static ILiteCollection<Row> Seed(LiteDatabase db)
        {
            var raw = db.GetCollection("rows");
            raw.Insert(new BsonDocument { ["_id"] = 1, ["Name"] = "abc", ["Part"] = "abc" });
            raw.Insert(new BsonDocument { ["_id"] = 2, ["Name"] = "abc", ["Part"] = BsonValue.Null });
            raw.Insert(new BsonDocument { ["_id"] = 3, ["Name"] = "abc" });
            raw.Insert(new BsonDocument { ["_id"] = 4, ["Name"] = "abc", ["Part"] = 5 });

            return db.GetCollection<Row>("rows");
        }

        [Fact]
        public void A_row_whose_compared_field_is_not_a_string_does_not_match_and_does_not_abort_the_query()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var rows = Seed(db);
            const StringComparison Mode = StringComparison.Ordinal;

            rows.Find(x => x.Name.StartsWith(x.Part, Mode)).Select(x => x.Id).Should().Equal(1);
            rows.Find(x => x.Name.EndsWith(x.Part, Mode)).Select(x => x.Id).Should().Equal(1);
#if !NETFRAMEWORK
            // string.Contains(string, StringComparison) does not exist on .NET Framework
            rows.Find(x => x.Name.Contains(x.Part, Mode)).Select(x => x.Id).Should().Equal(1);
#endif
            rows.Find(x => x.Name.Equals(x.Part, Mode)).Select(x => x.Id).Should().Equal(1);
            rows.Find(x => string.Equals(x.Name, x.Part, Mode)).Select(x => x.Id).Should().Equal(1);
            rows.Find(x => x.Name.IndexOf(x.Part, Mode) == 0).Select(x => x.Id).Should().Equal(1);
            rows.Find(x => x.Name.IndexOf(x.Part, 0, Mode) == 0).Select(x => x.Id).Should().Equal(1);
        }

        [Fact]
        public void DeleteMany_with_a_row_supplied_text_removes_only_the_matching_row()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var rows = Seed(db);

            rows.DeleteMany(x => x.Name.StartsWith(x.Part, StringComparison.OrdinalIgnoreCase)).Should().Be(1);
            rows.Count().Should().Be(3);
        }

        [Fact]
        public void A_constant_null_text_is_still_a_caller_error()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var rows = Seed(db);
            string text = null;

            Action query = () => rows.Find(x => x.Name.StartsWith(text, StringComparison.Ordinal)).ToArray();

            query.Should().Throw<ArgumentNullException>();
        }
    }
}
