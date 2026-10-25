using System;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1851Replay_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Indexed_dates_support_multiple_aggregates_and_sort_replay(bool utc)
        {
            using var db = new LiteDatabase(":memory:");
            db.UtcDate = utc;
            var early = new DateTime(2024, 5, 6, 12, 0, 0, DateTimeKind.Utc);
            var late = new DateTime(2024, 7, 4, 12, 0, 0, DateTimeKind.Utc);
            var rows = db.GetCollection("rows");
            rows.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["stamp"] = early },
                new BsonDocument { ["_id"] = 2, ["stamp"] = late }
            });
            rows.EnsureIndex("stamp");
            var aggregate = rows.Query().Select("{ low: MIN(*.stamp), high: MAX(*.stamp) }");
            Assert.Equal("index", aggregate.GetPlan()["lookup"]["loader"].AsString);
            var result = aggregate.ToDocuments().Single();
            Assert.Equal(early, result["low"].AsDateTime.ToUniversalTime());
            Assert.Equal(late, result["high"].AsDateTime.ToUniversalTime());
            Assert.Equal(utc ? DateTimeKind.Utc : DateTimeKind.Local, result["high"].AsDateTime.Kind);
            var sorted = rows.Query().OrderBy("DAY(stamp)").Select("stamp");
            Assert.Equal("index", sorted.GetPlan()["lookup"]["loader"].AsString);
            Assert.Equal(new[] { late, early }, sorted.ToDocuments().Select(row => row["stamp"].AsDateTime.ToUniversalTime()));
        }

        [Fact]
        public void Grouping_repeated_local_hours_preserves_each_utc_instant()
        {
            using var db = new LiteDatabase(":memory:");
            db.UtcDate = false;
            var dates = new[]
            {
                new DateTime(2024, 11, 3, 5, 30, 0, DateTimeKind.Utc),
                new DateTime(2024, 11, 3, 6, 0, 0, DateTimeKind.Utc),
                new DateTime(2024, 11, 3, 6, 30, 0, DateTimeKind.Utc)
            };
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("stamp");
            rows.Insert(dates.Select((date, index) => new BsonDocument { ["_id"] = index + 1, ["stamp"] = date }));
            var query = rows.Query().GroupBy("stamp").Select("{ stamp: @key, count: COUNT(*.stamp) }");
            Assert.Equal("index", query.GetPlan()["lookup"]["loader"].AsString);
            var groups = query.ToDocuments().ToArray();
            Assert.Equal(dates, groups.Select(group => group["stamp"].AsDateTime.ToUniversalTime()));
            Assert.All(groups, group => Assert.Equal(1, group["count"].AsInt32));
        }
    }
}
