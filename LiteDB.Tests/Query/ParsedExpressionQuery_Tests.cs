using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class ParsedExpressionQuery_Tests
    {
        [Fact]
        public void Repeated_string_predicates_keep_current_parameters_and_multikey_semantics()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");
            var data = Enumerable.Range(1, 20).Select(i => new Row { Id = i, Score = i, Values = new[] { i, i + 1 } }).ToArray();
            rows.InsertBulk(data);
            const string source = "Score >= @minimum AND Values ANY IN @keys";
            for (var indexed = 0; indexed < 2; indexed++)
            {
                foreach (var minimum in new[] { 0, 5, 12, 0 })
                {
                    var keys = new[] { minimum + 1, minimum + 3 };
                    var expected = data.Where(x => x.Score >= minimum && x.Values.Any(v => keys.Contains(v))).Select(x => x.Id);
                    var parameters = new BsonDocument { ["minimum"] = minimum, ["keys"] = new BsonArray(keys.Select(x => new BsonValue(x))) };
                    rows.Query().Where(source, parameters).ToArray().Select(x => x.Id).Should().BeEquivalentTo(expected);
                }
                rows.EnsureIndex(x => x.Score);
                rows.EnsureIndex("values", "Values[*]");
            }
        }

        [Fact]
        public void Repeated_predicates_use_live_index_metadata_and_rebuilt_collation()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(new ConnectionString { Filename = file.Filename, Collation = Collation.Binary }))
            {
                seed.GetCollection<Row>("rows").InsertBulk(new[] { new Row { Id = 1, Name = "Alpha" }, new Row { Id = 2, Name = "alpha" } });
            }
            using var db = new LiteDatabase(file.Filename);
            var rows = db.GetCollection<Row>("rows");
            const string source = "Name = @name";
            var parameters = new BsonDocument { ["name"] = "alpha" };
            string Index() => rows.Query().Where(source, parameters).GetPlan()["index"]["name"].AsString;
            Index().Should().Be("_id");
            Index().Should().Be("_id");
            rows.EnsureIndex("names", "Name");
            Index().Should().Be("names");
            rows.Query().Where(source, parameters).ToArray().Select(x => x.Id).Should().Equal(2);
            db.Rebuild(new RebuildOptions { Collation = new Collation("en-US/IgnoreCase") });
            rows.Query().Where(source, parameters).ToArray().Select(x => x.Id).Should().BeEquivalentTo(new[] { 1, 2 });
            rows.DropIndex("names");
            Index().Should().Be("_id");
        }

        [Fact]
        public void Concurrent_queries_and_interleaved_enumerators_read_their_own_bindings()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");
            rows.InsertBulk(Enumerable.Range(1, 20).Select(i => new Row { Id = i }));
            const string source = "_id >= @minimum AND _id < @maximum";
            Parallel.For(0, 100, i =>
            {
                var minimum = i % 18 + 1;
                rows.Query().Where(source, new BsonDocument { ["minimum"] = minimum, ["maximum"] = minimum + 2 })
                    .ToArray().Select(x => x.Id).Should().Equal(minimum, minimum + 1);
                rows.FindById(minimum).Id.Should().Be(minimum);
            });
            using var first = rows.Query().Where(source, new BsonDocument { ["minimum"] = 1, ["maximum"] = 3 }).ToEnumerable().GetEnumerator();
            using var second = rows.Query().Where(source, new BsonDocument { ["minimum"] = 8, ["maximum"] = 10 }).ToEnumerable().GetEnumerator();
            first.MoveNext().Should().BeTrue();
            second.MoveNext().Should().BeTrue();
            first.Current.Id.Should().Be(1);
            second.Current.Id.Should().Be(8);
            first.MoveNext().Should().BeTrue();
            first.Current.Id.Should().Be(2);
        }

        [Fact]
        public void Reused_computed_index_expressions_validate_and_update_current_keys()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");
            rows.InsertBulk(new[] { new Row { Id = 1, Name = "one" }, new Row { Id = 2, Name = "four" } });
            for (var i = 0; i < 3; i++) rows.EnsureIndex("length", "LENGTH(Name)");
            for (var i = 0; i < 3; i++) rows.Query().Where("LENGTH(Name) = @0", 3).Single().Id.Should().Be(1);
            rows.Update(new Row { Id = 1, Name = "three" });
            rows.Query().Where("LENGTH(Name) = @0", 3).ToArray().Should().BeEmpty();
            rows.Query().Where("LENGTH(Name) = @0", 5).Single().Id.Should().Be(1);
        }

        public class Row
        {
            public int Id { get; set; }
            public int Score { get; set; }
            public string Name { get; set; }
            public int[] Values { get; set; }
        }
    }
}
