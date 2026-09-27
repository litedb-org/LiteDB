using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedModeAdmissionReview_Tests
    {
#if NET8_0_OR_GREATER
        [MappedTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Repeated_read_only_queries_do_not_initialize_or_replace_mode_identity(bool mismatched)
        {
            using var file = new MappedTestFile();
            using (var db = new LiteDatabase(file))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "preserved" });
            var path = file.Filename + "-shared-mode";
            File.Delete(path);
            if (mismatched)
                using (SharedModeGuard.Open(file, true, SharedMutexNameStrategy.Sha1Hash)) { }
            var identity = mismatched ? File.ReadAllBytes(path) : null;
            var data = File.ReadAllBytes(file);
            using (var engine = new SharedEngine(new EngineSettings { Filename = file, ReadOnly = true }))
            using (var db = new LiteDatabase(engine))
            {
                for (var i = 0; i < 5; i++)
                    db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("preserved");
                if (mismatched) File.ReadAllBytes(path).Should().Equal(identity);
                else File.Exists(path).Should().BeFalse();
                engine.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Protected);
                engine.GetDiagnostics().CoordinatedReadHits.Should().Be(0);
                File.Exists(SharedCoordinationFallback.PagePath(file)).Should().BeFalse();
            }
            File.ReadAllBytes(file).Should().Equal(data);
            using var cold = new LiteDatabase(file);
            cold.GetCollection("rows").Count().Should().Be(1);
        }
#endif

        [Fact]
        public void Unavailable_direct_guard_preserves_permission_exception_and_data()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var data = File.ReadAllBytes(file.Filename);
            var path = file.Filename + "-shared-mode";
            File.Delete(path);
            Directory.CreateDirectory(path);
            try
            {
                Action open = () => { using var db = new LiteDatabase(file.Filename); };
                open.Should().Throw<UnauthorizedAccessException>();
                File.ReadAllBytes(file.Filename).Should().Equal(data);
            }
            finally { Directory.Delete(path); }
            using var cold = new LiteDatabase(file.Filename);
            cold.GetCollection("rows").Count().Should().Be(1);
        }

        [Fact]
        public void Direct_requires_file_locking_even_without_shared_artifacts()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            using var file = new TempFile();
            AppContext.TryGetSwitch("System.IO.DisableFileLocking", out var before);
            AppContext.SetSwitch("System.IO.DisableFileLocking", true);
            try
            {
                Action open = () => { using var db = new LiteDatabase(file.Filename); };
                open.Should().Throw<PlatformNotSupportedException>().WithMessage("*Direct*");
                File.Exists(file.Filename).Should().BeFalse();
                File.Exists(file.Filename + "-shared-mode").Should().BeFalse();
            }
            finally { AppContext.SetSwitch("System.IO.DisableFileLocking", before); }
            using var cold = new LiteDatabase(file.Filename);
            cold.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
        }

#if NET8_0_OR_GREATER
        [MappedFact]
        public async Task Read_only_mapped_attachment_holds_admission_until_connection_disposal()
        {
            using var file = new MappedTestFile();
            using (var db = new LiteDatabase(file))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            using (SharedModeGuard.Open(file, true, SharedMutexNameStrategy.Default)) { }
            using (var engine = new SharedEngine(new EngineSettings { Filename = file, ReadOnly = true }))
            using (var db = new LiteDatabase(engine))
            {
                for (var i = 0; i < 5; i++) db.GetCollection("rows").Count().Should().Be(1);
                engine.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Mapped);
                await MvccProcess.Run("mode-direct-rejected", file, null);
                // Diagnostics may observe disposal concurrently, without touching a freed view.
                await Task.WhenAll(Task.Run(() =>
                {
                    for (var i = 0; i < 500; i++) engine.GetDiagnostics();
                }), Task.Run(() => engine.Dispose()));
                engine.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Disposed);
            }
            using var cold = new LiteDatabase(file);
            cold.GetCollection("rows").Count().Should().Be(1);
        }
#endif

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Rebuild_candidates_do_not_leave_mode_sidecars(bool fail)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-mode-rebuild-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var filename = Path.Combine(directory, "data.db");
            try
            {
                using (var db = new LiteDatabase(filename))
                {
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "preserved" });
                    RebuildService.SimulateInstallFailure = stage =>
                    {
                        if (fail && stage == "before-temp-install") throw new IOException("injected installation failure");
                    };
                    try
                    {
                        Action rebuild = () => db.Rebuild();
                        if (fail) rebuild.Should().Throw<IOException>();
                        else rebuild();
                    }
                    finally { RebuildService.SimulateInstallFailure = null; }
                }
                Directory.GetFiles(directory, "*-shared-mode").Should().Equal(filename + "-shared-mode");
                using var cold = new LiteDatabase(filename);
                cold.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("preserved");
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
