using System;
using System.Linq;
using System.Threading;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2787_Tests
    {
        [Fact]
        public void Shared_transaction_releases_mutex_while_owner_thread_is_still_alive()
        {
            using var file = new TempFile();
            using var ready = new ManualResetEventSlim();
            using var releaseOwner = new ManualResetEventSlim();
            using var writerFinished = new ManualResetEventSlim();
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared });
            Exception ownerFailure = null;
            Exception writerFailure = null;
            var owner = new Thread(() =>
            {
                try
                {
                    db.BeginTrans().Should().BeTrue();
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "first" });
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "second" });
                    db.Commit().Should().BeTrue();
                }
                catch (Exception ex) { ownerFailure = ex; }
                finally { ready.Set(); releaseOwner.Wait(TimeSpan.FromSeconds(10)); }
            }) { IsBackground = true };
            var writer = new Thread(() =>
            {
                try { db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3, ["value"] = "other thread" }); }
                catch (Exception ex) { writerFailure = ex; }
                finally { writerFinished.Set(); }
            }) { IsBackground = true };
            var finishedBeforeOwnerExit = false;
            var writerStarted = false;
            owner.Start();
            try
            {
                ready.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                writer.Start();
                writerStarted = true;
                finishedBeforeOwnerExit = writerFinished.Wait(TimeSpan.FromSeconds(2));
            }
            finally
            {
                releaseOwner.Set();
                owner.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                if (writerStarted) writer.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            }
            using (new AssertionScope())
            {
                ownerFailure.Should().BeNull();
                writerFailure.Should().BeNull();
                finishedBeforeOwnerExit.Should().BeTrue("OS abandonment after owner exit is not a correct mutex release");
                db.GetCollection("rows").FindAll().OrderBy(x => x["_id"].AsInt32)
                    .Select(x => x["value"].AsString).Should().Equal("first", "second", "other thread");
            }
        }
    }
}
