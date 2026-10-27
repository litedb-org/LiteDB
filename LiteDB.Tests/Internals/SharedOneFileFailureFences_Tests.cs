#if NET8_0_OR_GREATER
using System;
using System.IO;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileFailureFences_Tests
    {
        [OneFileTheory]
        [InlineData("wal-before-durable-flush")]
        [InlineData("wal-after-durable-flush")]
        [InlineData("wal-before-batch-flush")]
        public void Terminal_stop_cannot_publish_or_clear_fences_and_releases_WRITE(string cut)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.BeginTrans().Should().BeTrue();
            db.GetCollection("docs").Update(Row(1, 1)).Should().BeTrue();
            var core = Core(engine);
            var state = State(core);
            var table = engine.MappedReaderTable;
            var before = table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion);
            var reached = false;
            var published = false;
            state.AtCrashPoint = stage =>
            {
                if (stage != cut) return;
                reached = true;
                throw new IOException("terminal mapped barrier failure");
            };
            SharedMemoryReaderTable.PublicationStage = stage =>
            {
                if (stage == "commit-version" || stage == "writer-end") published = true;
            };
            try
            {
                Action commit = () => db.Commit();
                commit.Should().Throw<IOException>();
                reached.Should().BeTrue();
                state.Stopped.Should().BeTrue();
                core.Close().Should().BeEmpty("an already stopped close is not a successful storage-health witness");
                published.Should().BeFalse();
                table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().Be(before);
                Fenced(table);
                files.WriteIsFree();
            }
            finally { state.AtCrashPoint = null; SharedMemoryReaderTable.PublicationStage = null; }
            // The interrupted confirmation is outcome unknown; independently read
            // it once, then require the same complete outcome through both repairs.
            var value = db.GetCollection("docs").FindById(1)["value"].AsInt32;
            value.Should().BeOneOf(0, 1);
            for (var repeat = 0; repeat < 2; repeat++) { Verify(db, value); db.Checkpoint(); Quiet(table); }
        }

        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Cache_only_or_unsupported_sync_never_advertises_a_new_durable_version(bool unsupported)
        {
            using var files = new SharedOneFilePublicationFixture();
            var settings = files.Settings;
            settings.DurableCommits = unsupported;
            using var engine = new SharedEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.BeginTrans().Should().BeTrue();
            db.GetCollection("docs").Update(Row(1, 1)).Should().BeTrue();
            var table = engine.MappedReaderTable;
            var before = table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion);
            var hits = 0;
            NativeFileSync.SimulateErrno = path =>
            {
                if (path != files.Log || !unsupported) return 0;
                hits++;
                return 95;
            };
            try
            {
                db.Commit().Should().BeTrue(); // documented weaker commit remains usable
                if (unsupported) hits.Should().BeGreaterThan(0);
                table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().Be(before);
                Fenced(table);
                var retained = engine.Pin;
                retained.Should().NotBeNull("a successful cache-only commit may retain its writable core");
                ((Action)files.WriteIsFree).Should().Throw<IOException>("the retained core still excludes native WRITE");
                retained.RequestRelease(force: false);
                retained.WaitReleased();
                Fenced(table); // handoff must not turn an unproven sync into a durable publication
                files.WriteIsFree();
            }
            finally { NativeFileSync.SimulateErrno = null; }
            // A fresh protected open must make its own real barrier, even when the
            // preceding engine recorded degraded durability in process state.
            db.Timeout.Should().BeGreaterThan(TimeSpan.Zero);
            Quiet(table);
            Verify(db, 1);
        }

        [OneFileTheory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void Changed_live_authority_refuses_an_already_open_writer_and_preserves_accepted_snapshots(int word)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var old = engine.Query("docs", new Query());
            old.Read().Should().BeTrue();
            db.BeginTrans().Should().BeTrue();
            var table = engine.MappedReaderTable;
            var map = table.Authority.Map;
            var offset = word == 0 ? SharedMemoryLayout.Control : word == 1 ? 0 : word == 2 ? SharedMemoryLayout.Page : word == 3 ? 8 : 48;
            var previous = map.Load(offset);
            var data = File.ReadAllBytes(files.Filename);
            var wal = File.Exists(files.Log) ? File.ReadAllBytes(files.Log) : Array.Empty<byte>();
            map.Store(offset, 0);
            try
            {
                Action write = () => db.GetCollection("docs").Update(Row(1, 1));
                write.Should().Throw<IOException>();
                File.ReadAllBytes(files.Filename).Should().Equal(data);
                (File.Exists(files.Log) ? File.ReadAllBytes(files.Log) : Array.Empty<byte>()).Should().Equal(wal);
                SharedMemoryReaderTable.Scan(table.Authority).Should().BeNull();
            }
            finally { map.Store(offset, previous); }
            db.Rollback();
            var count = 0;
            do { old.Current["value"].AsInt32.Should().Be(0); old.Current["payload"].AsString.Should().Be(new string('x', 1024)); count++; }
            while (old.Read());
            count.Should().Be(160);
            Verify(db, 0);
        }
    }
}
#endif
