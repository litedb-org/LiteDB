#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedModeDiagnostics_Tests
    {
        [MappedFact]
        public void Diagnostics_observe_cache_leases_pressure_revocation_and_recreation()
        {
            using var file = new MappedTestFile();
            using var events = new SharedEvents();
            using (var engine = new SharedEngine(new EngineSettings { Filename = file })
                { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
            using (var db = new LiteDatabase(engine))
            {
                engine.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Uninitialized);
                Seed(db);
                Warm(db);
                var before = engine.GetDiagnostics();
                before.ReadPath.Should().Be(SharedReadPath.Mapped);
                before.ProcessMappedParticipants.Should().Be(1);
                before.CoordinatedReadHits.Should().BeGreaterThan(0);
                before.CoordinatedReadMisses.Should().BeGreaterThan(0);
                before.ActiveSnapshotLeases.Should().Be(0);
                using (var held = db.GetCollection("rows").FindAll().GetEnumerator())
                {
                    held.MoveNext().Should().BeTrue();
                    engine.GetDiagnostics().ActiveSnapshotLeases.Should().Be(1);
                }
                engine.GetDiagnostics().ActiveSnapshotLeases.Should().Be(0);
                engine.ForceCoordinatedYield = true;
                db.GetCollection("rows").FindById(0);
                engine.GetDiagnostics().WriterYields.Should().BeGreaterThan(0);
                db.GetCollection("rows").Update(new BsonDocument { ["_id"] = 0, ["value"] = 2 });
                engine.GetDiagnostics().WriterPressureRequests.Should().BeGreaterThan(0);
                SharedCoordinationFallback.Revoke(file);
                engine.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Revoked);
                Warm(db);
            }
            using (var engine = new SharedEngine(new EngineSettings { Filename = file }))
            using (var db = new LiteDatabase(engine))
            {
                Warm(db);
                engine.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Mapped);
                events.ThrowOnEvent = true;
                engine.Dispose();
                engine.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Disposed);
                engine.GetDiagnostics().ProcessMappedParticipants.Should().Be(0);
            }
            events.States.Should().Contain(new[] { "attached", "detached", "created", "retired", "revoked" });
        }

        [MappedTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Direct_and_different_mutex_writers_cannot_mutate_a_mapped_snapshot(string password)
        {
            using var file = new MappedTestFile();
            using (var engine = new SharedEngine(new EngineSettings { Filename = file, Password = password }))
            using (var db = new LiteDatabase(engine))
            {
                Seed(db);
                Warm(db);
                using var held = db.GetCollection("rows").FindAll().GetEnumerator();
                held.MoveNext().Should().BeTrue();
                engine.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Mapped);
                var data = TempFile.ReadAllBytesShared(file);
                var logPath = FileHelper.GetLogFile(file);
                var log = File.Exists(logPath) ? TempFile.ReadAllBytesShared(logPath) : null;
                Action direct = () => { using var other = new LiteEngine(new EngineSettings { Filename = file, Password = password }); };
                direct.Should().Throw<IOException>().WithMessage("*Cannot safely admit*");
                await MvccProcess.Run("mode-direct-rejected", file, password);
                await MvccProcess.Run("mode-mutex-rejected", file, password);
                TempFile.ReadAllBytesShared(file).Should().Equal(data);
                if (log != null) TempFile.ReadAllBytesShared(logPath).Should().Equal(log);
                var count = 1;
                while (held.MoveNext()) { held.Current["value"].AsInt32.Should().Be(0); count++; }
                count.Should().Be(150);
            }
            using var reopened = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            reopened.GetCollection("rows").Count().Should().Be(150);
            reopened.GetCollection("untouched").FindById(1)["value"].AsInt32.Should().Be(42);
            reopened.GetCollection("rows").Find("value = 0").Count().Should().Be(150);
        }

        [MappedFact]
        public async Task Direct_first_blocks_shared_then_process_death_releases_admission()
        {
            using var file = new MappedTestFile();
            using (var db = new LiteDatabase(file)) Seed(db);
            using (var child = new MvccProcess("mode-direct-hold", file, null))
            {
                await child.Expect("ready");
                using var engine = new SharedEngine(new EngineSettings { Filename = file });
                using var db = new LiteDatabase(engine);
                Action write = () => db.GetCollection("rows").DeleteAll();
                write.Should().Throw<IOException>().WithMessage("*Cannot safely admit*");
                await child.Kill();
            }
            using var recovered = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared });
            recovered.GetCollection("rows").Count().Should().Be(150);
            recovered.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 151, ["value"] = 0 });
            recovered.GetCollection("rows").Count().Should().Be(151);
        }

        [MappedFact]
        public void Equivalent_mutex_names_share_admission_and_diagnostics_are_process_local()
        {
            using var file = new MappedTestFile();
            using var first = new SharedEngine(new EngineSettings { Filename = file });
            using var db = new LiteDatabase(first);
            Seed(db);
            Warm(db);
            using (var second = new SharedEngine(new EngineSettings
                { Filename = file, SharedMutexNameStrategy = SharedMutexNameStrategy.UriEscape }))
            using (var peer = new LiteDatabase(second))
            {
                Warm(peer);
                second.GetDiagnostics().ProcessMappedParticipants.Should().Be(2);
                first.GetDiagnostics().ProcessMappedParticipants.Should().Be(2);
                peer.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 200, ["value"] = 7 });
                db.GetCollection("rows").FindById(200)["value"].AsInt32.Should().Be(7);
                foreach (var upgrade in new[] { false, true })
                {
                    Action open = () =>
                    {
                        using var direct = new LiteEngine(new EngineSettings
                            { Filename = file, ReadOnly = true, Upgrade = upgrade, AutoRebuild = !upgrade });
                    };
                    open.Should().Throw<IOException>().WithMessage("*Cannot safely admit*");
                }
            }
            first.GetDiagnostics().ProcessMappedParticipants.Should().Be(1);
        }

        [MappedFact]
        public void Failed_open_and_rebuild_release_guard_and_idle_identity_can_change()
        {
            using var file = new MappedTestFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file, Password = "secret" })) Seed(db);
            Action wrongPassword = () => { using var db = new LiteDatabase(file); db.GetCollection("rows").Count(); };
            wrongPassword.Should().Throw<LiteException>();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file, Password = "secret" }))
            {
                db.Rebuild();
                db.GetCollection("rows").Count().Should().Be(150);
            }
            using var engine = new SharedEngine(new EngineSettings
                { Filename = file, Password = "secret", SharedMutexNameStrategy = SharedMutexNameStrategy.Sha1Hash });
            using var shared = new LiteDatabase(engine);
            Warm(shared);
            engine.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Mapped);
        }

        [MappedFact]
        public void Protected_read_only_fallback_needs_no_mode_file_and_preserves_storage()
        {
            using var file = new MappedTestFile();
            using (var db = new LiteDatabase(file)) Seed(db);
            File.Delete(file.Filename + "-shared-mode");
            var original = File.ReadAllBytes(file);
            using var engine = new SharedEngine(new EngineSettings { Filename = file, ReadOnly = true })
                { CoordinationArchitectureOverride = System.Runtime.InteropServices.Architecture.Arm };
            using var dbRead = new LiteDatabase(engine);
            Warm(dbRead);
            var diagnostics = engine.GetDiagnostics();
            diagnostics.ReadPath.Should().Be(SharedReadPath.Protected);
            diagnostics.FallbackReason.Should().Contain("architecture");
            diagnostics.CoordinatedReadHits.Should().Be(0);
            File.Exists(file.Filename + "-shared-mode").Should().BeFalse();
            File.ReadAllBytes(file).Should().Equal(original);
        }

        [MappedFact]
        public void Repeated_fallback_has_stable_reason_without_stack_traces()
        {
            using var file = new MappedTestFile();
            using (var db = new LiteDatabase(file)) Seed(db);
            using (SharedModeGuard.Open(file, true, SharedMutexNameStrategy.Default)) { }
            using var events = new SharedEvents();
            SharedCoordinationFile.CreationStage = (path, stage) =>
            {
                if (stage == "created") throw new IOException("injected mapping failure");
            };
            try
            {
                using var engine = new SharedEngine(new EngineSettings { Filename = file, ReadOnly = true });
                using var db = new LiteDatabase(engine);
                Warm(db);
                Warm(db);
                engine.GetDiagnostics().FallbackReason.Should().Be("IOException: injected mapping failure");
                events.FallbackReasons.Should().Equal("IOException: injected mapping failure");
                engine.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Protected);
            }
            finally { SharedCoordinationFile.CreationStage = null; }
            using var cold = new LiteDatabase(file);
            cold.GetCollection("untouched").FindById(1)["value"].AsInt32.Should().Be(42);
        }

        private static void Seed(LiteDatabase db)
        {
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(0, 150).Select(id => new BsonDocument { ["_id"] = id, ["value"] = 0 }));
            rows.EnsureIndex("value");
            db.GetCollection("untouched").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
        }

        private static void Warm(LiteDatabase db)
        {
            for (var i = 0; i < 4; i++) Assert.NotNull(db.GetCollection("rows").FindById(0));
        }

        private sealed class SharedEvents : EventListener
        {
            internal bool ThrowOnEvent;
            internal readonly List<string> States = new List<string>();
            internal readonly List<string> FallbackReasons = new List<string>();
            protected override void OnEventSourceCreated(EventSource source)
            {
                if (source.Name == "LiteDB-Shared") EnableEvents(source, EventLevel.Informational);
            }
            protected override void OnEventWritten(EventWrittenEventArgs data)
            {
                if (data.EventId != 1) return;
                lock (States)
                {
                    States.Add((string)data.Payload[1]);
                    if ((string)data.Payload[1] == "fallback") FallbackReasons.Add((string)data.Payload[2]);
                }
                if (ThrowOnEvent) throw new InvalidOperationException("diagnostic listener failure");
            }
        }
    }
}
#endif
