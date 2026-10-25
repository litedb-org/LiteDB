using System;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2822_Tests
    {
        [Fact]
        public void Commit_from_another_thread_does_not_silently_report_no_transaction()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            using var pending = new ManualResetEventSlim();
            using var finish = new ManualResetEventSlim();
            Exception ownerFailure = null;
            var owner = new Thread(() =>
            {
                try
                {
                    db.BeginTrans().Should().BeTrue();
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "pending" });
                    pending.Set();
                    if (!finish.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                    db.Rollback(); // Cleanup on the original thread if cross-thread commit was rejected.
                }
                catch (Exception ex) { ownerFailure = ex; }
                finally { pending.Set(); }
            }) { IsBackground = true };
            owner.Start();
            bool committed = false;
            Exception failure = null;
            try
            {
                pending.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                failure = Record.Exception(() => committed = db.Commit());
            }
            finally
            {
                finish.Set();
                owner.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            }
            ownerFailure.Should().BeNull();
            if (failure != null)
            {
                failure.Should().BeOfType<LiteException>();
                db.GetCollection("rows").Count().Should().Be(0);
            }
            else
            {
                committed.Should().BeTrue("silently returning false hides an outstanding transaction after a thread hop");
                db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("pending");
            }
        }
    }
}
