using System;
using System.IO;
using System.Linq;
using LiteDB.Engine;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection("DirectProcessHost")]
    public class DirectColdOpen_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-cold-open-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public DirectColdOpen_Tests() => Directory.CreateDirectory(_directory);

        [Fact]
        public async Task Existing_file_uses_native_admission_without_waiting_for_path_bootstrap()
        {
            using (var seed = new LiteDatabase(Filename))
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "original" });
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var bootstrap = Task.Run(() =>
            {
                using var path = NativeAdmission.EnterPath(Filename);
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            });
            try
            {
                held.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                var open = Task.Run(() =>
                {
                    using var db = new LiteDatabase(Filename);
                    db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("original");
                });
                (await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(2)))).Should().BeSameAs(open);
                await open;
            }
            finally { release.Set(); await bootstrap; }
        }

        [Fact]
        public async Task New_file_waits_for_path_bootstrap_before_creating_any_storage()
        {
            using var started = new ManualResetEventSlim();
            Task open;
            using (NativeAdmission.EnterPath(Filename))
            {
                open = Task.Run(() =>
                {
                    started.Set();
                    using var db = new LiteDatabase(Filename);
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed" });
                    db.GetCollection("rows").EnsureIndex("value");
                });
                started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                Thread.Sleep(150);
                open.IsCompleted.Should().BeFalse();
                Directory.GetFiles(_directory).Should().BeEmpty();
            }
            await open;
            using var reopened = new LiteDatabase(Filename);
            reopened.GetCollection("rows").FindOne(Query.EQ("value", "committed"))["_id"].AsInt32.Should().Be(1);
        }

        [Fact]
        public void Deferred_system_collections_remain_local_to_each_facade()
        {
            using var first = new LiteEngine(Filename);
            using var second = new LiteEngine(Filename);
            first.RegisterSystemCollection("$custom", () => new[] { new BsonDocument { ["owner"] = 1 } });
            second.RegisterSystemCollection("$custom", () => new[] { new BsonDocument { ["owner"] = 2 } });
            using var one = new LiteDatabase(first, disposeOnClose: false);
            using var two = new LiteDatabase(second, disposeOnClose: false);
            one.Execute("select $ from $custom").Single()["owner"].AsInt32.Should().Be(1);
            two.Execute("select $ from $custom").Single()["owner"].AsInt32.Should().Be(2);
            using (var columns = one.Execute("select $ from $cols")) columns.Read().Should().BeTrue();
            two.Execute("select $ from $database").Single().IsDocument.Should().BeTrue();
        }

#if !NETFRAMEWORK
        [Fact]
        public async Task Parent_symlink_and_canonical_creation_share_the_path_bootstrap()
        {
            var alias = _directory + "-alias";
            Directory.CreateSymbolicLink(alias, _directory);
            try
            {
                using var started = new ManualResetEventSlim();
                Task open;
                using (NativeAdmission.EnterPath(Path.Combine(alias, "test.db")))
                {
                    open = Task.Run(() =>
                    {
                        started.Set();
                        using var db = new LiteDatabase(Filename);
                        db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed" });
                    });
                    started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    Thread.Sleep(150);
                    open.IsCompleted.Should().BeFalse();
                    Directory.GetFiles(_directory).Should().BeEmpty();
                }
                await open;
                using var reopened = new LiteDatabase(Path.Combine(alias, "test.db"));
                reopened.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("committed");
            }
            finally { Directory.Delete(alias); }
        }
#endif

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
