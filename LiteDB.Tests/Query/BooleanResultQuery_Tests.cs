using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class BooleanResultQuery_Tests
    {
        [Fact]
        public void Linq_and_sql_project_fresh_boolean_arrays_with_changing_bindings()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");
            var data = Enumerable.Range(1, 20).Select(i => new Row
            {
                Id = i, Score = i, Values = i == 1 ? new int[0] : Enumerable.Range(i % 4, 4).ToArray()
            }).ToArray();
            rows.InsertBulk(data);
            rows.EnsureIndex(x => x.Score);
            foreach (var minimum in new[] { 0, 3, 1, 0 })
            {
                var maximum = minimum + 2;
                var linq = rows.Query().Where(x => x.Score > 0)
                    .Select(x => new { x.Id, Flags = x.Values.Select(v => v >= minimum && v < maximum).ToArray() })
                    .ToArray();
                using var reader = db.Execute("SELECT { _id: _id, flags: ARRAY(MAP(Values => @ >= @minimum AND @ < @maximum)) } " +
                    "FROM rows WHERE Score > 0", new BsonDocument { ["minimum"] = minimum, ["maximum"] = maximum });
                var sql = reader.ToArray();
                foreach (var row in data)
                {
                    var expected = row.Values.Select(v => v >= minimum && v < maximum).ToArray();
                    linq.Single(x => x.Id == row.Id).Flags.Should().Equal(expected);
                    sql.Single(x => x["_id"] == row.Id)["flags"].AsArray.Select(x => x.AsBoolean).Should().Equal(expected);
                }
                sql.Single(x => x["_id"] == 2)["flags"].AsArray[0] = false;
                linq.Single(x => x.Id == 2).Flags.Should().Equal(data[1].Values.Select(v => v >= minimum && v < maximum));
            }
        }

        [Fact]
        public void Persisted_boolean_projection_fields_remain_independent_during_index_updates()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            var projection = BsonExpression.Create("{ _id: Id, matched: Score > 1 AND Score <= 3 }");
            rows.InsertBulk(Enumerable.Range(1, 4).Select(i => projection.ExecuteScalar(
                new BsonDocument { ["Id"] = i, ["Score"] = i }).AsDocument));
            rows.EnsureIndex("matched");
            rows.Find("matched = true").Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 2, 3 });
            var changed = rows.FindById(2);
            changed["matched"] = false;
            rows.Update(changed);
            rows.Find("matched = true").Select(x => x["_id"].AsInt32).Should().Equal(3);
            rows.Find("matched = false").Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 2, 4 });
        }

        public class Row
        {
            public int Id { get; set; }
            public int Score { get; set; }
            public int[] Values { get; set; }
        }
    }
}
