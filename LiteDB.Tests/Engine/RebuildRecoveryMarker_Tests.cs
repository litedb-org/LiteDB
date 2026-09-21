using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class RebuildRecoveryMarker_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-marker-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "data.db");
        private string Marker => RebuildRecovery.GetMarkerFilename(Filename);

        public RebuildRecoveryMarker_Tests()
        {
            Directory.CreateDirectory(_directory);
        }

        [Theory]
        [InlineData("before-log-backup")]
        [InlineData("after-log-backup")]
        [InlineData("before-source-backup")]
        [InlineData("after-source-backup")]
        [InlineData("before-temp-install")]
        [InlineData("after-temp-install")]
        public void Opens_are_guarded_during_every_installation_phase(string target)
        {
            using var db = Seed();
            var observed = false;
            RebuildService.SimulateInstallFailure = phase =>
            {
                if (phase != target) return;
                observed = true;
                File.ReadAllText(Marker).Should().Contain(Path.GetFullPath(Filename));
                Action open = () =>
                {
                    using var engine = new LiteEngine(new EngineSettings
                    {
                        Filename = Filename, Upgrade = true, AutoRebuild = true
                    });
                };
                open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
            };

            db.Rebuild();
            observed.Should().BeTrue();
            File.Exists(Marker).Should().BeFalse();
            db.GetCollection("rows").Count().Should().Be(1);
            // Existing backups must not be mistaken for an incomplete installation.
            db.Rebuild();
            db.GetCollection("rows").Count().Should().Be(1);
            File.Exists(Marker).Should().BeFalse();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Marker_creation_failure_does_not_move_the_original_files(bool conflictingMarker)
        {
            using var db = Seed();
            var sourceBytes = File.ReadAllBytes(Filename);
            var log = FileHelper.GetLogFile(Filename);
            var logBytes = File.ReadAllBytes(log);
            RebuildService.SimulateInstallFailure = phase =>
            {
                if (phase != "before-recovery-marker") return;
                if (conflictingMarker) File.WriteAllText(Marker, "existing marker");
                else throw new IOException("marker creation failure");
            };
            Action rebuild = () => db.Rebuild();
            rebuild.Should().Throw<IOException>();
            RebuildService.SimulateInstallFailure = null;
            File.ReadAllBytes(Filename).Should().Equal(sourceBytes);
            File.ReadAllBytes(log).Should().Equal(logBytes);
            File.Exists(FileHelper.GetSuffixFile(Filename, "-backup", false)).Should().BeFalse();
            if (conflictingMarker)
            {
                File.ReadAllText(Marker).Should().Be("existing marker");
                Action read = () => db.GetCollection("rows").Count();
                read.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
            }
            else
            {
                File.Exists(Marker).Should().BeFalse();
                db.GetCollection("rows").Count().Should().Be(1);
            }
        }

        [Fact]
        public void Marker_flush_failure_leaves_the_original_pair_untouched_and_access_blocked()
        {
            using var db = Seed();
            var sourceBytes = File.ReadAllBytes(Filename);
            var log = FileHelper.GetLogFile(Filename);
            var logBytes = File.ReadAllBytes(log);
            RebuildService.SimulateInstallFailure = phase =>
            {
                if (phase == "before-recovery-marker-flush") throw new IOException("marker flush failure");
            };
            Action rebuild = () => db.Rebuild();
            rebuild.Should().Throw<IOException>().WithMessage("marker flush failure");
            RebuildService.SimulateInstallFailure = null;
            File.ReadAllBytes(Filename).Should().Equal(sourceBytes);
            File.ReadAllBytes(log).Should().Equal(logBytes);
            Action read = () => db.GetCollection("rows").Count();
            read.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
        }

        [Fact]
        public void Failed_marker_cleanup_preserves_the_original_error_and_blocks_opens()
        {
            using var db = Seed();
            RebuildService.SimulateInstallFailure = phase =>
            {
                if (phase == "after-temp-install") throw new IOException("install failure");
                if (phase == "before-recovery-marker-delete") throw new IOException("cleanup failure");
            };
            Action rebuild = () => db.Rebuild();
            var error = rebuild.Should().Throw<IOException>().WithMessage("install failure").Which;
            error.Data["LiteDB.Rebuild.RollbackErrors"].Should().BeOfType<AggregateException>()
                .Which.InnerExceptions.Should().ContainSingle().Which.Message.Should().Be("cleanup failure");
            RebuildService.SimulateInstallFailure = null;
            Action read = () => db.GetCollection("rows").Count();
            read.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
            // The complete original pair was restored; removing only the guard is safe here.
            File.Delete(Marker);
            db.GetCollection("rows").Count().Should().Be(1);
        }

        [Fact]
        public void A_briefly_locked_marker_does_not_undo_or_block_a_completed_rebuild()
        {
            // Only Windows refuses to delete a file that is open without FileShare.Delete, which is
            // how a virus scanner or sync client inspecting the new marker looks to the rebuild.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            using var db = Seed();
            FileStream scanner = null;
            RebuildService.SimulateInstallFailure = phase =>
            {
                if (phase != "before-log-backup") return;
                scanner = new FileStream(Marker, FileMode.Open, FileAccess.Read, FileShare.Read);
                Task.Delay(300).ContinueWith(_ => scanner.Dispose());
            };

            db.Rebuild(new RebuildOptions { Password = "new-password" });
            RebuildService.SimulateInstallFailure = null;

            scanner.Should().NotBeNull();
            File.Exists(Marker).Should().BeFalse();
            db.GetCollection("rows").Count().Should().Be(1);
            using var fresh = new LiteDatabase(new ConnectionString { Filename = Filename, Password = "new-password" });
            fresh.GetCollection("rows").Count().Should().Be(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Empty_marker_blocks_new_file_creation_and_rebuild_service(bool directoryMarker)
        {
            if (directoryMarker) Directory.CreateDirectory(Marker);
            else File.WriteAllBytes(Marker, Array.Empty<byte>());

            Action open = () =>
            {
                using var db = new LiteEngine(new EngineSettings { Filename = Filename, AutoRebuild = true, Upgrade = true });
            };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
            Action rebuild = () => new RebuildService(new EngineSettings { Filename = Filename });
            rebuild.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
            File.Exists(Filename).Should().BeFalse();
            File.Exists(FileHelper.GetLogFile(Filename)).Should().BeFalse();
        }

        [Fact]
        public void Caller_streams_do_not_consult_an_unused_filename_marker()
        {
            File.WriteAllText(Marker, "unrelated file database");
            using var stream = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings { Filename = Filename, DataStream = stream });
            engine.Insert("rows", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32).Should().Be(1);
            File.Exists(Filename).Should().BeFalse();
        }

#if !NETFRAMEWORK
        [Theory]
        [InlineData(251, false)]
        [InlineData(255, true)]
        public void Marker_probe_does_not_reduce_supported_database_filename_lengths(int length, bool readOnly)
        {
            // Windows limits the whole path, so a name this long cannot exist below the temp
            // directory at all. The per-name limit this guards is a Linux and macOS concern.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            using (var db = Seed()) db.Checkpoint();
            var filename = Path.Combine(_directory, new string('x', length - 3) + ".db");
            File.Copy(Filename, filename);
            using var reopened = new LiteDatabase(new ConnectionString { Filename = filename, ReadOnly = readOnly });
            reopened.GetCollection("rows").Count().Should().Be(1);
            if (!readOnly) reopened.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
        }
#endif

        private LiteDatabase Seed()
        {
            var db = new LiteDatabase(new ConnectionString { Filename = Filename, Connection = ConnectionType.Shared });
            db.CheckpointSize = 0;
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            return db;
        }

        public void Dispose()
        {
            RebuildService.SimulateInstallFailure = null;
            Directory.Delete(_directory, true);
        }
    }
}
