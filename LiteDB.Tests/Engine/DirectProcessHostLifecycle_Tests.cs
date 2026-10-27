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
