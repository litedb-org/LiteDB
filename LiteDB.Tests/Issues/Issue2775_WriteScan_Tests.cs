using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2775_WriteScan_Tests
    {
        private const int Documents = 5000;
        private static readonly TimeSpan _watchdog = TimeSpan.FromSeconds(30);

        [Fact]
        public void UpdateMany_moving_the_scanned_key_forward_updates_each_document_once()
        {
            using var db = CreateDatabase(out var rows);

            RunWithWatchdog(() => rows.UpdateMany("{k:$.k+1, n:$.n+1}", "$.k BETWEEN 10 AND 20")).Should().Be(550);

            rows.Count("$.n > 1").Should().Be(0);
            rows.Count("$.n = 1").Should().Be(550);
            SumOfKeys(db).Should().Be(248050);
        }

        [Fact]
        public void UpdateMany_moving_every_key_past_the_end_of_an_open_range_terminates()
        {
            using var db = CreateDatabase(out var rows);

            RunWithWatchdog(() => rows.UpdateMany("{k:$.k+1000, n:$.n+1}", "$.k >= 0")).Should().Be(Documents);

            rows.Count("$.n = 1").Should().Be(Documents);
            SumOfKeys(db).Should().Be(247500 + 1000 * Documents);
        }

        [Fact]
        public void UpdateMany_inside_an_explicit_transaction_updates_each_document_once()
        {
            using var db = CreateDatabase(out var rows);

            RunWithWatchdog(() =>
            {
                db.BeginTrans();
                var updated = rows.UpdateMany("{k:$.k+1, n:$.n+1}", "$.k BETWEEN 10 AND 20");
                db.Commit();
                return updated;
            }).Should().Be(550);

            rows.Count("$.n > 1").Should().Be(0);
            SumOfKeys(db).Should().Be(248050);
        }

        [Fact]
        public void Sql_update_moving_the_scanned_key_forward_updates_each_document_once()
        {
            using var db = CreateDatabase(out var rows);

            RunWithWatchdog(() => db.Execute("UPDATE rows SET k=$.k+3, n=$.n+1 WHERE $.k>90 AND $.k<95").Current.AsInt32)
                .Should().Be(200);

            rows.Count("$.n > 1").Should().Be(0);
            SumOfKeys(db).Should().Be(247500 + 3 * 200);
        }

        [Fact]
        public void Descending_write_cursor_moving_the_scanned_key_backward_visits_each_document_once()
        {
            using var db = CreateDatabase(out var rows);

            var visited = RunWithWatchdog(() =>
            {
                db.BeginTrans();
                var count = 0;
                foreach (var doc in rows.Query().Where("$.k BETWEEN 10 AND 20").OrderByDescending("$.k").ForUpdate().ToEnumerable())
                {
                    doc["k"] = doc["k"].AsInt32 - 1;
                    doc["n"] = doc["n"].AsInt32 + 1;
                    rows.Update(doc);
                    count++;
                }
                db.Commit();
                return count;
            });

            visited.Should().Be(550);
            rows.Count("$.n > 1").Should().Be(0);
            SumOfKeys(db).Should().Be(247500 - 550);
        }

        [Fact]
        public void DeleteMany_over_a_scanned_range_deletes_each_document_once()
        {
            using var db = CreateDatabase(out var rows);

            RunWithWatchdog(() => rows.DeleteMany("$.k BETWEEN 10 AND 20")).Should().Be(550);

            rows.Count().Should().Be(Documents - 550);
            rows.Count("$.k BETWEEN 10 AND 20").Should().Be(0);
        }

        private static LiteDatabase CreateDatabase(out ILiteCollection<BsonDocument> rows)
        {
            var db = new LiteDatabase(":memory:");
            rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(0, Documents).Select(i => new BsonDocument { ["_id"] = i, ["k"] = i % 100, ["n"] = 0 }));
            rows.EnsureIndex("k");
            return db;
        }

        private static int SumOfKeys(LiteDatabase db)
        {
            return db.Execute("SELECT SUM(*.k) AS s FROM rows").ToArray().Single()["s"].AsInt32;
        }

        // A revisiting scan never ends, so the write runs off-thread and a hang fails instead of blocking the suite.
        private static int RunWithWatchdog(Func<int> write)
        {
            var task = Task.Run(write);
            task.Wait(_watchdog).Should().BeTrue("a write driven by an index scan must terminate");
            return task.Result;
        }
    }
}
