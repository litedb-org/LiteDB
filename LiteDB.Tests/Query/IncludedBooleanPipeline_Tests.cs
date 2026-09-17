using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class IncludedBooleanPipeline_Tests
    {
        [Fact]
        public void Reused_sql_includes_replay_aggregates_with_current_bindings()
        {
            using var db = IncludedBooleanIndex_Tests.CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("key", "Owner.Score");
            const string sql = "SELECT { n: COUNT(*), total: SUM(*.Owner.Manager.Score), again: SUM(*.Owner.Manager.Score), first: FIRST(*.Ref.Name) } " +
                "FROM rows INCLUDE Owner.Manager, Ref WHERE (owner.score >= @low AND owner.score < @high) OR owner.score = 8 ORDER BY owner.score DESC";
            for (var low = 2; low < 5; low++)
            {
                var parameters = new BsonDocument { ["low"] = low, ["high"] = low + 2 };
                using var explain = db.Execute("EXPLAIN " + sql, parameters);
                explain.Read().Should().BeTrue();
                explain.Current["index"]["name"].AsString.Should().Be("key");
                using var reader = db.Execute(sql, parameters);
                reader.Read().Should().BeTrue();
                var expected = Enumerable.Range(1, 40).Where(i => (i % 10 >= low && i % 10 < low + 2) || i % 10 == 8).ToArray();
                reader.Current["n"].AsInt32.Should().Be(expected.Length);
                reader.Current["total"].AsInt32.Should().Be(expected.Sum(i => 101 + i % 4));
                reader.Current["again"].Should().Be(reader.Current["total"]);
                reader.Current["first"].AsString.Should().BeOneOf("odd", "even");
            }
            db.GetCollection("owners").UpdateMany("{ Score: Score + 100 }", "true");
            using var refreshed = db.Execute(sql, new BsonDocument { ["low"] = 4, ["high"] = 6 });
            refreshed.Read().Should().BeTrue();
            refreshed.Current["total"].AsInt32.Should().Be(Enumerable.Range(1, 40)
                .Where(i => i % 10 == 4 || i % 10 == 5 || i % 10 == 8).Sum(i => 201 + i % 4));
        }

        [Theory]
        [InlineData("en-US/None", "_id")]
        [InlineData("en-US/IgnoreCase", "label")]
        public void Shared_guard_values_use_the_current_collation(string collation, string expectedIndex)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation(collation) });
            db.GetCollection("owners").InsertBulk(new[] { 1, 2 }.Select(i => new BsonDocument { ["_id"] = i, ["Score"] = 100 + i }));
            var rows = db.GetCollection("rows");
            rows.InsertBulk(new[] { 1, 2 }.Select(i => new BsonDocument
            {
                ["_id"] = i, ["Label"] = i == 1 ? "even" : "EVEN", ["Ref"] = IncludedBooleanIndex_Tests.Reference(i - 1)
            }));
            const string predicate = "(Label = 'even' AND Ref.Score = 101) OR (Label = 'EVEN' AND Ref.Score = 102)";
            rows.EnsureIndex("label", "Label");
            var query = rows.Query().Include("Ref").Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be(expectedIndex);
            query.ToArray().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 2 });
        }

        [Fact]
        public void Missing_references_keep_their_original_include_results()
        {
            using var db = IncludedBooleanIndex_Tests.CreateDatabase();
            db.GetCollection("owners").Delete(1);
            var rows = db.GetCollection("rows");
            const string predicate = "(Score >= 2 AND Score < 4) OR Score = 8";
            var expected = rows.Query().Include("Ref").Where(predicate).ToArray().OrderBy(x => x["_id"]).Select(x => x.ToString()).ToArray();
            rows.EnsureIndex("key", "Score");
            var query = rows.Query().Include("Ref").Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be("key");
            query.ToArray().OrderBy(x => x["_id"]).Select(x => x.ToString()).Should().Equal(expected);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void For_update_includes_do_not_revisit_keys_moved_into_later_ranges(bool unique)
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("owners").Insert(new BsonDocument { ["_id"] = 1, ["Name"] = "resolved" });
            var rows = db.GetCollection("rows");
            rows.InsertBulk(new[] { 1, 8 }.Select(i => new BsonDocument
            {
                ["_id"] = i, ["Score"] = i, ["Hits"] = 0, ["Ref"] = IncludedBooleanIndex_Tests.Reference(0)
            }));
            rows.EnsureIndex("key", "Score", unique);
            var query = rows.Query().Include("Ref").Where("(Score >= 1 AND Score <= 20 AND (Score < 10 OR Score > 10)) OR Score = 50").ForUpdate();
            query.GetPlan()["index"]["name"].AsString.Should().Be("key");
            db.BeginTrans();
            var visited = 0;
            foreach (var item in query.ToEnumerable())
            {
                item["Ref"]["Name"].AsString.Should().Be("resolved");
                var stored = rows.FindById(item["_id"]);
                stored["Score"] = stored["Score"].AsInt32 + 10;
                stored["Hits"] = stored["Hits"].AsInt32 + 1;
                rows.Update(stored).Should().BeTrue();
                visited++;
            }
            visited.Should().Be(2);
            rows.FindAll().OrderBy(x => x["_id"]).Select(x => x["Score"].AsInt32).Should().Equal(11, 18);
            rows.FindAll().Select(x => x["Hits"].AsInt32).Should().Equal(1, 1);
            db.Rollback();
            rows.FindAll().OrderBy(x => x["_id"]).Select(x => x["Score"].AsInt32).Should().Equal(1, 8);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Included_ranges_survive_page_release_rollback_and_reopen(string password)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Password = password, CacheSize = 65536, TransactionPageLimit = 1 };
            const string predicate = "(Score >= 10 AND Score <= 80 AND (Score < 20 OR Score > 70)) OR Score = 5";
            using (var db = new LiteDatabase(connection))
            {
                db.GetCollection("owners").Insert(new BsonDocument { ["_id"] = 1, ["Name"] = "resolved", ["Payload"] = new string('y', 800) });
                var rows = db.GetCollection("rows");
                rows.InsertBulk(Enumerable.Range(1, 2000).Select(i => new BsonDocument
                {
                    ["_id"] = i, ["Score"] = i % 100, ["Payload"] = new string('x', 800), ["Ref"] = IncludedBooleanIndex_Tests.Reference(0)
                }));
                rows.EnsureIndex("key", "Score");
                db.Checkpoint();
                db.BeginTrans();
                var updated = 0;
                foreach (var item in rows.Query().Include("Ref").Where(predicate).ForUpdate().ToEnumerable())
                {
                    var stored = rows.FindById(item["_id"]);
                    stored["Score"] = stored["Score"].AsInt32 + 61;
                    rows.Update(stored);
                    updated++;
                }
                updated.Should().Be(420);
                db.Rollback();
                rows.Query().Include("Ref").Where(predicate).Count().Should().Be(420);
            }
            using (var db = new LiteDatabase(connection))
            {
                var query = db.GetCollection("rows").Query().Include("Ref").Where(predicate).OrderBy("Score", Query.Descending).Offset(190).Limit(40);
                query.GetPlan()["index"]["name"].AsString.Should().Be("key");
                var result = query.ToArray();
                var expected = Enumerable.Range(1, 2000).Select(i => i % 100).Where(i => (i >= 10 && i < 20) || (i > 70 && i <= 80) || i == 5)
                    .OrderByDescending(i => i).Skip(190).Take(40);
                result.Select(x => x["Score"].AsInt32).Should().Equal(expected);
                result.Select(x => x["Ref"]["Name"].AsString).Should().OnlyContain(x => x == "resolved");
            }
        }
    }
}
