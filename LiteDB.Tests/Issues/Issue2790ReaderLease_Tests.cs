using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2790ReaderLease_Tests
    {
        [Fact]
        public void Parallel_lazy_query_updates_leave_checkpoint_available()
        {
            using var db = new LiteDatabase(":memory:");
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(1, 500).Select(id => new BsonDocument { ["_id"] = id, ["updated"] = false }));
            Parallel.ForEach(rows.FindAll().Select(row => row["_id"].AsInt32),
                new ParallelOptions { MaxDegreeOfParallelism = 4 }, id =>
                {
                    var row = rows.FindById(id);
                    row["updated"] = true;
                    rows.Update(row).Should().BeTrue();
                });
            db.Checkpoint();
            rows.Count("updated = true").Should().Be(500);
        }

        [Fact]
        public async Task Awaiting_inside_lazy_query_does_not_leak_reader_lease()
        {
            using var db = new LiteDatabase(":memory:");
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(1, 20).Select(id => new BsonDocument { ["_id"] = id }));
            var seen = 0;
            foreach (var row in rows.FindAll())
            {
                await Task.Delay(1).ConfigureAwait(false);
                row["_id"].AsInt32.Should().Be(++seen);
            }
            seen.Should().Be(20);
            db.Checkpoint();
            rows.Count().Should().Be(20);
        }

        [Fact]
        public void Foreign_disposal_keeps_other_owner_cursors_and_foreign_transactions_alive()
        {
            using var db = new LiteDatabase(":memory:");
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new[] { new BsonDocument { ["_id"] = 1 }, new BsonDocument { ["_id"] = 2 } });
            using var first = rows.FindAll().GetEnumerator();
            using var second = rows.FindAll().GetEnumerator();
            first.MoveNext().Should().BeTrue();
            second.MoveNext().Should().BeTrue();
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    db.BeginTrans().Should().BeTrue();
                    first.Dispose();
                    first.Dispose();
                    rows.Insert(new BsonDocument { ["_id"] = 3 });
                    db.Rollback().Should().BeTrue();
                }
                catch (Exception error) { failure = error; }
            });
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
            failure.Should().BeNull();
            second.MoveNext().Should().BeTrue();
            second.Current["_id"].AsInt32.Should().Be(2);
            second.Dispose();
            rows.Count().Should().Be(2);
            db.Checkpoint();
        }

        [Fact]
        public void Reader_can_move_and_finish_on_another_thread()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new[] { new BsonDocument { ["_id"] = 1 }, new BsonDocument { ["_id"] = 2 } });
            using var cursor = rows.FindAll().GetEnumerator();
            cursor.MoveNext().Should().BeTrue();
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    cursor.MoveNext().Should().BeTrue();
                    cursor.Current["_id"].AsInt32.Should().Be(2);
                    cursor.MoveNext().Should().BeFalse();
                }
                catch (Exception error) { failure = error; }
            });
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
            failure.Should().BeNull();
            rows.Insert(new BsonDocument { ["_id"] = 3 });
            db.Checkpoint();
            rows.Count().Should().Be(3);
        }
    }
}
