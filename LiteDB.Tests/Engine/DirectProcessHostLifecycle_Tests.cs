using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Direct;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection("DirectProcessHost")]
    public class DirectProcessHostLifecycle_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-close-admission-" + Guid.NewGuid().ToString("N"));
        public DirectProcessHostLifecycle_Tests() => Directory.CreateDirectory(_directory);
        public void Dispose() => WindowsFileRelease.DeleteDirectory(_directory);
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Cold_open_releases_registry_and_wakes_same_file_waiters_after_success_or_failure(bool fail)
        {
            var filename = Path.Combine(_directory, "opening.db");
            using (var seed = new LiteDatabase(filename))
                seed.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "preserved" });
            // Native admission canonicalizes Darwin /var aliases. Compare the
            // hook's canonical identity while opening the original caller path.
            var identity = DatabaseFileIdentity.CanonicalPath(filename);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var waiting = new ManualResetEventSlim();
            var calls = 0;
            DirectProcessHost.BeforeHostConstruction = path =>
            {
                if (path != identity || Interlocked.Increment(ref calls) != 1) return;
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
                if (fail) throw new IOException("cold open failed");
            };
            DirectProcessHost.BeforeHostWait = path => { if (path == identity) waiting.Set(); };
            Task<LiteDatabase> first = null, peer = null;
            LiteDatabase opened = null, second = null;
            try
            {
                first = Task.Run(() => new LiteDatabase(filename));
                entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                peer = Task.Run(() => new LiteDatabase(filename));
                waiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                var unrelated = Task.Run(() =>
                {
                    using var db = new LiteDatabase(Path.Combine(_directory, "unrelated.db"));
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                });
                (await Task.WhenAny(unrelated, Task.Delay(5000))).Should().BeSameAs(unrelated);
                await unrelated;
                peer.IsCompleted.Should().BeFalse();
                release.Set();
                if (fail)
                {
                    Func<Task> opening = async () => { opened = await first; };
                    await opening.Should().ThrowAsync<IOException>();
                }
                else opened = await first;
                second = await peer;
                second.GetCollection("sentinel").FindById(1)["value"].AsString.Should().Be("preserved");
                if (opened != null)
                {
                    opened.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 2 });
                    second.GetCollection("sentinel").Count().Should().Be(2);
                }
            }
            finally
            {
                release.Set();
                if (first != null && opened == null) { try { opened = await first; } catch (IOException) { } }
                if (peer != null && second == null) second = await peer;
                opened?.Dispose();
                second?.Dispose();
                DirectProcessHost.BeforeHostConstruction = null;
                DirectProcessHost.BeforeHostWait = null;
            }
            using var cold = new LiteDatabase(filename);
            cold.GetCollection("sentinel").FindById(1)["value"].AsString.Should().Be("preserved");
            cold.GetCollection("sentinel").Count().Should().Be(fail ? 1 : 2);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Unrelated_database_opens_while_final_close_retains_same_file_exclusion(bool failClose)
        {
            var filename = Path.Combine(_directory, "closing.db");
            var otherFile = Path.Combine(_directory, "other.db");
            using (var other = new LiteDatabase(otherFile))
                other.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2, ["payload"] = "unrelated" });
            var failures = 0;
            var settings = new LiteDB.Engine.EngineSettings { Filename = filename };
            if (failClose) settings.CheckpointStage = stage =>
            {
                if (stage != "data-page") return;
                Interlocked.Increment(ref failures);
                throw new IOException("injected final close failure");
            };
            var db = new LiteDB.Engine.LiteEngine(settings);
            db.EnsureIndex("docs", "value", "$.value", false);
            db.Insert("docs", new[] { new BsonDocument { ["_id"] = 1, ["value"] = "committed", ["payload"] = new string('x', 9000) } }, BsonAutoId.Int32);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var started = new ManualResetEventSlim();
            var canonical = DatabaseFileIdentity.CanonicalPath(filename);
            DirectProcessHost.BeforeFinalClose = file =>
            {
                if (file != canonical) return;
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            };
            Task closing = null;
            Task opening = null;
            try
            {
                // Inject a real close checkpoint failure after the pause, rather
                // than throwing from the observer before storage cleanup runs.
                closing = Task.Run(db.Dispose);
                entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                opening = Task.Factory.StartNew(() =>
                {
                    started.Set();
                    using var other = new LiteDatabase(otherFile);
                    other.GetCollection("docs").FindById(2)["payload"].AsString.Should().Be("unrelated");
                    other.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 3, ["payload"] = "independent" });
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                (await Task.WhenAny(opening, Task.Delay(TimeSpan.FromSeconds(3)))).Should().BeSameAs(opening);
                await opening;
                using var probe = new DatabaseFileLock(filename, true, false);
                probe.Conflicts(DatabaseFileLock.Admission).Should().BeTrue();
            }
            finally
            {
                release.Set();
                DirectProcessHost.BeforeFinalClose = null;
                if (closing != null)
                {
                    await closing;
                }
                if (opening != null) await opening;
                db.Dispose();
            }
            failures.Should().Be(failClose ? 1 : 0, "the close checkpoint must reach the injected boundary");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var reopened = new LiteDatabase(filename);
                var doc = reopened.GetCollection("docs").FindOne(Query.EQ("value", "committed"));
                doc["_id"].AsInt32.Should().Be(1);
                doc["payload"].AsString.Should().Be(new string('x', 9000));
                reopened.GetCollection("docs").Count().Should().Be(1);
                using var other = new LiteDatabase(otherFile);
                other.GetCollection("docs").Count().Should().Be(2);
            }
        }
    }
}
