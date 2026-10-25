using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1344TransactionalWriter_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Transactional_incremental_writer_and_other_storage_operations_share_lock_order(bool delete)
        {
            using var db = new LiteDatabase(":memory:");
            db.Timeout = TimeSpan.FromSeconds(3);
            using (var source = new MemoryStream(new byte[] { 1 })) db.FileStorage.Upload("other", "other", source);
            Assert.True(db.BeginTrans());
            var writer = db.FileStorage.OpenWrite("incremental", "incremental");
            using var started = new ManualResetEventSlim();
            Exception failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    started.Set();
                    if (delete) Assert.True(db.FileStorage.Delete("other"));
                    else
                    {
                        using var source = new MemoryStream(new byte[] { 2 });
                        db.FileStorage.Upload("other", "other", source);
                    }
                }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            worker.Start();
            var committed = false;
            try
            {
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5)));
                writer.WriteByte(3);
                writer.Dispose();
                Assert.True(committed = db.Commit());
            }
            finally
            {
                try { if (!committed) db.Rollback(); }
                finally { Assert.True(worker.Join(TimeSpan.FromSeconds(10))); }
            }
            Assert.Null(failure);
            using var result = new MemoryStream();
            db.FileStorage.Download("incremental", result);
            Assert.Equal(new byte[] { 3 }, result.ToArray());
            Assert.Equal(!delete, db.FileStorage.Exists("other"));
        }

        [Fact]
        public void Opening_a_writer_preserves_file_snapshots_held_by_an_include_cursor()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            foreach (var id in new[] { "a", "b" })
            {
                using var source = new MemoryStream(new byte[] { 1 });
                db.FileStorage.Upload(id, id, source);
                rows.Insert(new BsonDocument
                {
                    ["_id"] = id, ["file"] = new BsonDocument { ["$ref"] = "_files", ["$id"] = id }
                });
            }
            Assert.True(db.BeginTrans());
            LiteFileStream<string> writer;
            using (var cursor = rows.Query().Include("$.file").ToEnumerable().GetEnumerator())
            {
                Assert.True(cursor.MoveNext());
                Assert.Equal("a", cursor.Current["file"]["filename"].AsString);
                writer = db.FileStorage.OpenWrite("new", "new");
                Assert.True(cursor.MoveNext());
                Assert.Equal("b", cursor.Current["file"]["filename"].AsString);
                Assert.False(cursor.MoveNext());
            }
            using (writer) writer.WriteByte(2);
            Assert.True(db.Commit());
            Assert.Equal(3, db.FileStorage.FindAll().Count());
        }

        [Fact]
        public void Opening_a_writer_preserves_an_active_file_cursor_until_it_finishes()
        {
            using var db = new LiteDatabase(":memory:");
            foreach (var id in new[] { "a", "b", "c" })
            {
                using var source = new MemoryStream(new byte[] { 1 });
                db.FileStorage.Upload(id, id, source);
            }
            Assert.True(db.BeginTrans());
            LiteFileStream<string> writer;
            using (var cursor = db.FileStorage.FindAll().GetEnumerator())
            {
                Assert.True(cursor.MoveNext());
                writer = db.FileStorage.OpenWrite("new", "new");
                Assert.True(cursor.MoveNext());
                Assert.True(cursor.MoveNext());
                Assert.False(cursor.MoveNext());
            }
            using (writer) writer.WriteByte(2);
            Assert.True(db.Commit());
            Assert.Equal(4, db.FileStorage.FindAll().Count());
        }
    }
}
