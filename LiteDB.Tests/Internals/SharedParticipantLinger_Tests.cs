#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFileRegistry")]
    public class SharedParticipantLinger_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(SharedMappedDirectory.Root, "litedb-linger-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public SharedParticipantLinger_Tests() => Directory.CreateDirectory(_directory);
        private EngineSettings Settings(int milliseconds) => new EngineSettings
        {
            Filename = Filename, SharedOneFile = true, SharedLingerMilliseconds = milliseconds
        };

        [OneFileFact]
        public void Reopen_reuses_the_participant_without_a_second_bootstrap()
        {
            var joins = 0;
            SharedMemoryAuthority.BootstrapStage = stage => { if (stage == "claimed") joins++; };
            try
            {
                SharedMemoryReaderTable table;
                using (var first = new SharedEngine(Settings(1000))) table = first.MappedReaderTable;
                table.Closed.Should().BeFalse();
                using (var second = new SharedEngine(Settings(1000))) second.MappedReaderTable.Should().BeSameAs(table);
                joins.Should().Be(1);
                SpinWait.SpinUntil(() => table.Closed, TimeSpan.FromSeconds(5)).Should().BeTrue();
                using var cold = new SharedEngine(Settings(0));
                cold.MappedReaderTable.Should().NotBeSameAs(table);
                joins.Should().Be(2);
            }
            finally { SharedMemoryAuthority.BootstrapStage = null; }
        }

        [OneFileFact]
        public void Expiry_releases_native_admission_and_preserves_committed_indexed_payloads()
        {
            SharedMemoryReaderTable table;
            using (var engine = new SharedEngine(Settings(100)))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                table = engine.MappedReaderTable;
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["key"] = "saved", ["payload"] = new string('x', 2048) });
                db.GetCollection("docs").EnsureIndex("key");
            }
            table.Closed.Should().BeFalse();
            SpinWait.SpinUntil(() => table.Closed, TimeSpan.FromSeconds(5)).Should().BeTrue();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var db = new LiteDatabase(Filename);
                db.GetCollection("docs").FindOne(Query.EQ("key", "saved"))["payload"].AsString.Should().Be(new string('x', 2048));
                db.GetCollection("docs").Count().Should().Be(1);
            }
        }

        [OneFileFact]
        public void Zero_preserves_synchronous_final_close()
        {
            SharedMemoryReaderTable table;
            using (var engine = new SharedEngine(Settings(0))) table = engine.MappedReaderTable;
            table.Closed.Should().BeTrue();
            using var db = new LiteDatabase(Filename);
            db.GetCollection("docs").Count().Should().Be(0);
        }

        [OneFileFact]
        public void Explicit_release_is_synchronous_and_does_not_close_active_facades()
        {
            SharedMemoryReaderTable table;
            using (var engine = new SharedEngine(Settings(1000)))
            {
                table = engine.MappedReaderTable;
                SharedParticipantRegistry.ReleaseIdle(Filename).Should().BeFalse();
                table.Closed.Should().BeFalse();
            }
            SharedParticipantRegistry.ReleaseIdle(Filename).Should().BeTrue();
            table.Closed.Should().BeTrue();
            SharedParticipantRegistry.ReleaseIdle(Filename).Should().BeFalse();
            using var db = new LiteDatabase(Filename);
            db.GetCollection("docs").Count().Should().Be(0);
        }

        [OneFileFact]
        public void Release_all_leaves_active_participants_usable()
        {
            using var active = new SharedEngine(new EngineSettings
            {
                Filename = Path.Combine(_directory, "active.db"), SharedOneFile = true, SharedLingerMilliseconds = 1000
            });
            SharedMemoryReaderTable idle;
            using (var engine = new SharedEngine(Settings(1000))) idle = engine.MappedReaderTable;
            idle.Closed.Should().BeFalse();
            SharedParticipantRegistry.ReleaseAllIdle();
            idle.Closed.Should().BeTrue();
            active.MappedReaderTable.Closed.Should().BeFalse();
            using var db = new LiteDatabase(active, disposeOnClose: false);
            db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            db.GetCollection("docs").Count().Should().Be(1);
        }

        [OneFileFact]
        public void Idle_settings_mismatch_replaces_the_participant()
        {
            SharedMemoryReaderTable old;
            using (var engine = new SharedEngine(Settings(1000))) old = engine.MappedReaderTable;
            old.Closed.Should().BeFalse();
            var settings = Settings(1000);
            settings.DurableCommits = false;
            using var next = new SharedEngine(settings);
            next.MappedReaderTable.Should().NotBeSameAs(old);
            old.Closed.Should().BeTrue();
            using var db = new LiteDatabase(next, disposeOnClose: false);
            db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
        }

        [OneFileFact]
        public void Read_only_retention_preserves_database_bytes()
        {
            using (var seed = new LiteDatabase(Filename))
                seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "saved" });
            var before = TempFile.ReadAllBytesShared(Filename);
            var settings = Settings(1000);
            settings.ReadOnly = true;
            SharedMemoryReaderTable table;
            using (var first = new SharedEngine(settings)) table = first.MappedReaderTable;
            table.Closed.Should().BeFalse();
            using (var second = new SharedEngine(settings))
            using (var db = new LiteDatabase(second, disposeOnClose: false))
            {
                second.MappedReaderTable.Should().BeSameAs(table);
                db.GetCollection("docs").FindById(1)["payload"].AsString.Should().Be("saved");
            }
            SharedParticipantRegistry.ReleaseIdle(Filename).Should().BeTrue();
            TempFile.ReadAllBytesShared(Filename).Should().Equal(before);
        }

        [OneFileFact]
        public void Live_result_cannot_be_released_as_idle()
        {
            using (var seed = new LiteDatabase(Filename))
            {
                for (var id = 0; id < 128; id++)
                    seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = id, ["payload"] = new string('x', 2048) });
            }
            using var engine = new SharedEngine(Settings(1000));
            var table = engine.MappedReaderTable;
            using (var result = engine.Query("docs", new Query()))
            {
                result.Read().Should().BeTrue();
                engine.Dispose();
                SharedParticipantRegistry.ReleaseIdle(Filename).Should().BeFalse();
                table.Closed.Should().BeFalse();
                var count = 1;
                while (result.Read())
                {
                    result.Current["payload"].AsString.Should().Be(new string('x', 2048));
                    count++;
                }
                count.Should().Be(128);
            }
            table.Closed.Should().BeFalse();
            SharedParticipantRegistry.ReleaseIdle(Filename).Should().BeTrue();
            table.Closed.Should().BeTrue();
        }

        [OneFileFact]
        public void Cancelled_expiry_cannot_close_a_reused_participant_or_its_next_idle_interval()
        {
            var first = new SharedEngine(Settings(1000));
            var table = first.MappedReaderTable;
            first.Dispose();
            table.Closed.Should().BeFalse();
            var oldTimer = typeof(SharedMemoryReaderTable).GetField("_lingerTimer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(table);
            var expire = typeof(SharedMemoryReaderTable).GetMethod("ExpireIdle", BindingFlags.Instance | BindingFlags.NonPublic);
            using (var second = new SharedEngine(Settings(1000)))
            {
                second.MappedReaderTable.Should().BeSameAs(table);
                expire.Invoke(table, new[] { oldTimer });
                table.Closed.Should().BeFalse();
            }
            // Force the already-queued old callback after a second idle timer exists.
            expire.Invoke(table, new[] { oldTimer });
            table.Closed.Should().BeFalse();
            SharedParticipantRegistry.ReleaseIdle(Filename).Should().BeTrue();
            table.Closed.Should().BeTrue();
        }

        [OneFileFact]
        public async Task Explicit_release_waits_for_in_progress_timer_storage_teardown()
        {
            SharedMemoryReaderTable table;
            using (var engine = new SharedEngine(Settings(100))) table = engine.MappedReaderTable;
            table.Closed.Should().BeFalse();
            using var reached = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            using var releaseEntered = new ManualResetEventSlim();
            Task release = null;
            SharedMemoryFinalClose.FinalCloseStage = stage =>
            {
                if (stage != "exclusive") return;
                reached.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Timer close was not resumed.");
            };
            try
            {
                reached.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                release = Task.Run(() => { releaseEntered.Set(); SharedParticipantRegistry.ReleaseIdle(Filename); });
                releaseEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                await Task.Delay(50);
                release.IsCompleted.Should().BeFalse();
                table.Closed.Should().BeFalse();
            }
            finally
            {
                resume.Set();
                try { if (release != null) await release; }
                finally { SharedMemoryFinalClose.FinalCloseStage = null; }
            }
            table.Closed.Should().BeTrue();
            using var cold = new LiteDatabase(Filename);
            cold.GetCollection("docs").Count().Should().Be(0);
        }

        public void Dispose()
        {
            SharedParticipantRegistry.ReleaseAllIdle();
            Directory.Delete(_directory, true);
        }
    }
}
#endif
