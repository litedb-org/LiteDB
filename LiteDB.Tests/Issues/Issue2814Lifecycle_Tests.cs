using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2814Lifecycle_Tests
    {
        [Fact]
        public async Task Later_commit_checkpoints_after_an_explicit_transaction_releases()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            db.CheckpointSize = 1;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1 });
            using var ready = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var reader = Task.Run(() =>
            {
                db.BeginTrans().Should().BeTrue();
                try
                {
                    rows.FindById(1)["_id"].AsInt32.Should().Be(1);
                    ready.Set();
                    release.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                }
                finally { db.Rollback(); }
            });
            var log = Path.ChangeExtension(file.Filename, null) + "-log.db";
            try
            {
                ready.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                rows.Insert(new BsonDocument { ["_id"] = 2 });
                new FileInfo(log).Length.Should().BeGreaterThan(0);
            }
            finally { release.Set(); await reader; }
            rows.Insert(new BsonDocument { ["_id"] = 3 });
            new FileInfo(log).Length.Should().Be(0);
            rows.Count().Should().Be(3);
        }
    }
}
