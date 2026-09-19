using System;
using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2790_Tests
    {
        [Fact]
        public void Disposing_cursor_on_another_thread_releases_reader_and_checkpoint_locks()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            db.Timeout = TimeSpan.FromSeconds(1);
            db.CheckpointSize = 0;
            var col = db.GetCollection("rows");
            col.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "original" });
            col.Insert(new BsonDocument { ["_id"] = 2, ["value"] = "second" });
            using var ready = new ManualResetEventSlim();
            using var released = new ManualResetEventSlim();
            IEnumerator<BsonDocument> cursor = null;
            Exception ownerFailure = null;
            var owner = new Thread(() =>
            {
                try
                {
                    cursor = col.FindAll().GetEnumerator();
                    cursor.MoveNext().Should().BeTrue();
                    cursor.Current["_id"].AsInt32.Should().Be(1);
                    ready.Set();
                    if (!released.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cursor release not signalled");
                    col.FindById(1)["value"].AsString.Should().Be("original");
                    col.Insert(new BsonDocument { ["_id"] = 3, ["value"] = "after cursor" });
                }
                catch (Exception ex) { ownerFailure = ex; }
                finally { ready.Set(); }
            }) { IsBackground = true };
            owner.Start();
            Exception disposeFailure = null;
            try
            {
                ready.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                ((object)cursor).Should().NotBeNull();
                disposeFailure = Record.Exception(() => cursor.Dispose());
            }
            finally
            {
                released.Set();
                owner.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            }
            using (new AssertionScope())
            {
                disposeFailure.Should().BeNull();
                ownerFailure.Should().BeNull();
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().NotThrow();
                col.Count().Should().Be(3);
            }
        }
    }
}
