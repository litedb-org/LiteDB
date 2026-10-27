using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Direct;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection("DirectProcessHost")]
    public class DirectProcessHost_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-direct-host-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public DirectProcessHost_Tests() => Directory.CreateDirectory(_directory);
        private LiteDatabase Open() => new LiteDatabase(Filename);

        [Fact]
        public void Facades_share_storage_and_release_only_on_last_dispose()
        {
            var first = Open();
            var second = Open();
            first.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed" });
            second.GetCollection("docs").FindById(1)["value"].AsString.Should().Be("committed");
            first.Dispose();
            first.Dispose();
            second.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 });
            second.Dispose();
            using var cold = Open();
            cold.GetCollection("docs").FindAll().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 2 });
        }

        [Fact]
        public void Foreign_facade_never_joins_or_completes_an_explicit_transaction()
        {
            using var first = Open();
            using var second = Open();
            first.BeginTrans().Should().BeTrue();
            first.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            Action insert = () => second.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 });
            insert.Should().Throw<InvalidOperationException>().WithMessage("*Another Direct facade*");
            second.Commit().Should().BeFalse();
            second.Rollback().Should().BeFalse();
            first.Rollback().Should().BeTrue();
            second.GetCollection("docs").Count().Should().Be(0);
            second.BeginTrans().Should().BeTrue();
            second.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 3 });
            first.Commit().Should().BeFalse();
            second.Commit().Should().BeTrue();
            Assert.NotNull(first.GetCollection("docs").FindById(3));
        }

        [Fact]
        public void Disposing_transaction_owner_rolls_back_without_damaging_peer_or_reopen()
        {
            using (var first = Open())
            using (var second = Open())
            {
                first.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
                first.BeginTrans();
                first.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2, ["large"] = new string('x', 30000) });
                first.Dispose();
                Assert.Null(second.GetCollection("docs").FindById(2));
                second.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 3 });
            }
            using var cold = Open();
            cold.GetCollection("docs").FindAll().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 3 });
        }

        [Theory]
        [InlineData("password")]
        [InlineData("readonly")]
        [InlineData("durability")]
        [InlineData("cache")]
        [InlineData("collation")]
        [InlineData("pages")]
        [InlineData("upgrade")]
        [InlineData("compact")]
        public void Incompatible_attach_fails_without_releasing_or_modifying_owner(string setting)
        {
            using var first = Open();
            first.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            var settings = new EngineSettings { Filename = Filename };
            switch (setting)
            {
                case "password": settings.Password = "wrong"; break;
                case "readonly": settings.ReadOnly = true; break;
                case "durability": settings.DurableCommits = false; break;
                case "cache": settings.CacheSize = 1024 * 1024; break;
                case "collation": settings.Collation = new Collation("en-US/None"); break;
                case "pages": settings.TransactionPageLimit = 1; break;
                case "upgrade": settings.Upgrade = true; break;
                case "compact": settings.CompactStorage = CompactStorageMode.Legacy; break;
            }
            Action attach = () => { using var engine = new LiteEngine(settings); };
            attach.Should().Throw<InvalidOperationException>().WithMessage("*Incompatible Direct engine settings*");
            Assert.NotNull(first.GetCollection("docs").FindById(1));
        }

        [Fact]
        public void Maintenance_requires_one_facade_and_rekeys_host_after_replacement()
        {
            using var first = Open();
            first.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            using (var second = Open())
            {
                Action rebuild = () => first.Rebuild();
                Action checkpoint = () => second.Checkpoint();
                rebuild.Should().Throw<InvalidOperationException>().WithMessage("*Close other Direct facades*");
                checkpoint.Should().Throw<InvalidOperationException>();
            }
            first.Rebuild();
            using var attached = new LiteDatabase(Filename);
            Assert.NotNull(attached.GetCollection("docs").FindById(1));
        }

        [Fact]
        public async Task Concurrent_attach_dispose_preserves_all_committed_rows()
        {
            using (var anchor = Open())
            {
                await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
                {
                    for (var i = 0; i < 20; i++)
                    {
                        using var db = Open();
                        db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = worker * 20 + i });
                    }
                })));
                anchor.GetCollection("docs").Count().Should().Be(160);
            }
            using var cold = Open();
            cold.GetCollection("docs").Count().Should().Be(160);
        }

        [Fact]
        public async Task Open_during_last_close_waits_then_opens_fresh()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var db = Open();
            db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            var canonical = DatabaseFileIdentity.CanonicalPath(Filename);
            DirectProcessHost.BeforeFinalClose = file =>
            {
                if (file != canonical) return;
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            };
            try
            {
                var closing = Task.Run(db.Dispose);
                entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                var opening = Task.Run(() => Open());
                await Task.Delay(50);
                opening.IsCompleted.Should().BeFalse();
                release.Set();
                await closing;
                using var fresh = await opening;
                Assert.NotNull(fresh.GetCollection("docs").FindById(1));
            }
            finally { release.Set(); DirectProcessHost.BeforeFinalClose = null; }
        }

        [Fact]
        public void Caller_streams_and_memory_keep_independent_engines()
        {
            using var a = new LiteDatabase(":memory:");
            using var b = new LiteDatabase(":memory:");
            a.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            b.GetCollection("docs").Count().Should().Be(0);
            using var data = new MemoryStream();
            using var streamDb = new LiteDatabase(data);
            streamDb.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 });
            streamDb.GetCollection("docs").Count().Should().Be(1);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
