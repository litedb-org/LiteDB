using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1344Composition_Tests
    {
        [Fact]
        public void Deleting_a_missing_file_preserves_an_incremental_writer()
        {
            using var db = new LiteDatabase(":memory:");
            var bytes = Enumerable.Range(0, LiteFileStream<string>.MAX_CHUNK_SIZE + 17).Select(i => (byte)i).ToArray();
            using (var writer = db.FileStorage.OpenWrite("target", "new"))
            {
                writer.Write(bytes, 0, bytes.Length);
                Assert.False(db.FileStorage.Exists("target"));
                Assert.False(db.FileStorage.Delete("target"));
                writer.WriteByte(99);
            }
            using var downloaded = new MemoryStream();
            db.FileStorage.Download("target", downloaded);
            Assert.Equal(bytes.Concat(new byte[] { 99 }).ToArray(), downloaded.ToArray());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Deletion_joins_caller_transaction_with_an_open_cursor(bool commit)
        {
            using var db = new LiteDatabase(":memory:");
            using (var source = new MemoryStream(new byte[] { 1, 2 })) db.FileStorage.Upload("target", "old", source);
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1 });
            rows.Insert(new BsonDocument { ["_id"] = 2 });
            Assert.True(db.BeginTrans());
            using (var cursor = rows.FindAll().GetEnumerator())
            {
                Assert.True(cursor.MoveNext());
                Assert.True(db.FileStorage.Delete("target"));
                Assert.True(cursor.MoveNext());
            }
            if (commit) Assert.True(db.Commit());
            else Assert.True(db.Rollback());
            Assert.Equal(!commit, db.FileStorage.Exists("target"));
            Assert.Equal(commit ? 0 : 1, db.GetCollection("_chunks").Count());
        }

        [Fact]
        public void Metadata_then_delete_in_a_caller_transaction_does_not_invert_upload_locks()
        {
            using var db = new LiteDatabase(":memory:");
            db.Timeout = TimeSpan.FromSeconds(3);
            using (var source = new MemoryStream(new byte[] { 1, 2 })) db.FileStorage.Upload("target", "old", source);
            Assert.True(db.BeginTrans());
            Assert.True(db.FileStorage.SetMetadata("target", new BsonDocument { ["changed"] = true }));
            using var started = new ManualResetEventSlim();
            Exception uploadFailure = null;
            var upload = new Thread(() =>
            {
                try
                {
                    started.Set();
                    using var source = new MemoryStream(new byte[] { 3, 4, 5 });
                    db.FileStorage.Upload("target", "new", source);
                }
                catch (Exception error) { uploadFailure = error; }
            }) { IsBackground = true };
            upload.Start();
            try
            {
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(SpinWait.SpinUntil(() => (upload.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5)), "Upload did not wait for the caller's metadata lock");
                Assert.True(db.FileStorage.Delete("target"));
                Assert.True(db.Commit());
            }
            finally
            {
                db.Rollback();
                Assert.True(upload.Join(TimeSpan.FromSeconds(15)));
            }
            Assert.Null(uploadFailure);
            using var downloaded = new MemoryStream();
            db.FileStorage.Download("target", downloaded);
            Assert.Equal(new byte[] { 3, 4, 5 }, downloaded.ToArray());
        }
    }
}
