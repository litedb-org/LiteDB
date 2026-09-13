using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1958_Tests
    {
        [Fact]
        public void Rebuild_cannot_discard_another_threads_transaction_or_poison_followup_writes()
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-1958-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "data.db");
            try
            {
                using (var db = new LiteDatabase(path))
                using (var ready = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                {
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "original" });
                    var owner = Task.Run(() => Record.Exception(() =>
                    {
                        db.BeginTrans().Should().BeTrue();
                        db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "pending" });
                        ready.Set();
                        release.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                        db.Commit().Should().BeTrue("rebuild must not silently discard the active transaction");
                    }));
                    Task<Exception> rebuild = null;
                    try
                    {
                        ready.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                        rebuild = Task.Run(() => Record.Exception(() => db.Rebuild()));
                        rebuild.Wait(TimeSpan.FromMilliseconds(200));
                    }
                    finally
                    {
                        release.Set();
                        owner.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                        if (rebuild != null) rebuild.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    }
                    owner.Result.Should().BeNull();
                    if (rebuild.Result != null) rebuild.Result.Should().BeOfType<LiteException>();
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3, ["value"] = "after rebuild" });
                }
                using var reopened = new LiteDatabase(path);
                var rows = reopened.GetCollection("rows");
                rows.Count().Should().Be(3);
                rows.FindById(1)["value"].AsString.Should().Be("original");
                rows.FindById(2)["value"].AsString.Should().Be("pending");
                rows.FindById(3)["value"].AsString.Should().Be("after rebuild");
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
