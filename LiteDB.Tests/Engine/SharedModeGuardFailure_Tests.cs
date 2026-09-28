#if NET8_0_OR_GREATER
using System;
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
    public class SharedModeGuardFailure_Tests
    {
        [MappedTheory]
        [InlineData("mode-initializing")]
        [InlineData("mode-truncated")]
        [InlineData("mode-written")]
        [InlineData("mode-flushed")]
        public async Task Interrupted_mode_identity_initialization_is_repeatable(string stage)
        {
            using var file = new MappedTestFile();
            using (var db = new LiteDatabase(file))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "preserved" });
            var data = File.ReadAllBytes(file);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var child = new MvccProcess("mode-initialize", file, null, stage);
                await child.Expect("ready");
                await child.Kill();
                File.ReadAllBytes(file).Should().Equal(data);
            }
            using (var engine = new SharedEngine(new EngineSettings { Filename = file }))
            using (var db = new LiteDatabase(engine))
            {
                for (var i = 0; i < 4; i++) db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("preserved");
                engine.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Mapped);
            }
            using var reopened = new LiteDatabase(file);
            reopened.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("preserved");
        }

        [MappedFact]
        public void Initialization_io_failure_does_not_mutate_data_or_leak_admission()
        {
            using var file = new MappedTestFile();
            using (var db = new LiteDatabase(file)) db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var data = File.ReadAllBytes(file);
            SharedCoordinationFile.CreationStage = (path, stage) =>
            {
                if (stage == "mode-truncated") throw new IOException("injected mode write failure");
            };
            try
            {
                using var engine = new SharedEngine(new EngineSettings { Filename = file });
                using var db = new LiteDatabase(engine);
                Action write = () => db.GetCollection("rows").DeleteAll();
                write.Should().Throw<IOException>();
            }
            finally { SharedCoordinationFile.CreationStage = null; }
            File.ReadAllBytes(file).Should().Equal(data);
            using var reopened = new LiteDatabase(file);
            reopened.GetCollection("rows").Count().Should().Be(1);
        }

        [MappedFact]
        public void Read_only_mapping_initialization_failure_retains_protected_reads()
        {
            using var file = new MappedTestFile();
            using (var db = new LiteDatabase(file)) db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var data = File.ReadAllBytes(file);
            using (SharedModeGuard.Open(file, true, SharedMutexNameStrategy.Default)) { }
            SharedCoordinationFile.CreationStage = (path, stage) =>
            {
                if (stage == "created") throw new IOException("read-only coordination storage");
            };
            try
            {
                using var engine = new SharedEngine(new EngineSettings { Filename = file, ReadOnly = true });
                using var db = new LiteDatabase(engine);
                for (var i = 0; i < 4; i++) db.GetCollection("rows").Count().Should().Be(1);
                engine.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Protected);
                engine.GetDiagnostics().FallbackReason.Should().Contain("read-only coordination storage");
                File.ReadAllBytes(file).Should().Equal(data);
            }
            finally { SharedCoordinationFile.CreationStage = null; }
        }

        [MappedFact]
        public void Readers_outliving_connection_keep_direct_writers_out()
        {
            using var file = new MappedTestFile();
            using var engine = new SharedEngine(new EngineSettings { Filename = file });
            using var db = new LiteDatabase(engine);
            db.GetCollection("rows").InsertBulk(Enumerable.Range(0, 150).Select(id => new BsonDocument { ["_id"] = id }));
            for (var i = 0; i < 4; i++) db.GetCollection("rows").FindById(0);
            using (var held = db.GetCollection("rows").FindAll().GetEnumerator())
            {
                held.MoveNext().Should().BeTrue();
                engine.Dispose();
                Action open = () => { using var direct = new LiteEngine(file); };
                open.Should().Throw<IOException>().WithMessage("*Cannot safely admit*");
                var count = 1;
                while (held.MoveNext()) count++;
                count.Should().Be(150);
            }
            using var recovered = new LiteDatabase(file);
            recovered.GetCollection("rows").Count().Should().Be(150);
        }

        [MappedTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Rebuild_holds_admission_through_install_and_releases_it_after_failure(bool fail)
        {
            using var file = new MappedTestFile();
            using var direct = new LiteDatabase(file);
            direct.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var reached = false;
            RebuildService.SimulateInstallFailure = stage =>
            {
                if (stage != "before-recovery-marker") return;
                reached = true;
                using var shared = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared });
                Action write = () => shared.GetCollection("rows").DeleteAll();
                write.Should().Throw<IOException>().WithMessage("*Cannot safely admit*");
                if (fail) throw new IOException("injected rebuild failure");
            };
            try
            {
                Action rebuild = () => direct.Rebuild();
                if (fail) rebuild.Should().Throw<IOException>();
                else rebuild();
                reached.Should().BeTrue();
            }
            finally { RebuildService.SimulateInstallFailure = null; }
            direct.Dispose();
            using var recovered = new LiteDatabase(file);
            recovered.GetCollection("rows").Count().Should().Be(1);
        }

        [MappedFact]
        public void Unknown_admission_file_is_preserved_and_writes_fail_closed()
        {
            using var file = new MappedTestFile();
            using (var db = new LiteDatabase(file)) db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var data = File.ReadAllBytes(file);
            var path = file.Filename + "-shared-mode";
            var foreign = new byte[] { 9, 17, 29, 35 };
            File.WriteAllBytes(path, foreign);
            using (var db = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
            {
                Action write = () => db.GetCollection("rows").DeleteAll();
                write.Should().Throw<IOException>().WithInnerException<IOException>().WithMessage("*Unrecognized Shared mode*");
            }
            File.ReadAllBytes(path).Should().Equal(foreign);
            File.ReadAllBytes(file).Should().Equal(data);
            using var readOnly = new LiteDatabase(new ConnectionString
                { Filename = file, ReadOnly = true, Connection = ConnectionType.Shared });
            for (var i = 0; i < 3; i++) readOnly.GetCollection("rows").Count().Should().Be(1);
            File.ReadAllBytes(path).Should().Equal(foreign);
        }

        [MappedFact]
        public void Existing_pre_guard_participant_blocks_direct_without_touching_authority()
        {
            using var file = new MappedTestFile();
            using (var db = new LiteDatabase(file)) db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var data = File.ReadAllBytes(file);
            var livePath = SharedCoordinationFallback.LivePath(file);
            File.WriteAllBytes(livePath, SharedCoordinationProtocol.CreateParticipation(file));
            using (var legacy = new FileStream(livePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Action open = () => { using var direct = new LiteEngine(file); };
                open.Should().Throw<IOException>().WithMessage("*Cannot safely admit*");
                File.ReadAllBytes(file).Should().Equal(data);
            }
            using var reopened = new LiteDatabase(file);
            reopened.GetCollection("rows").Count().Should().Be(1);
        }
    }
}
#endif
