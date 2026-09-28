using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class RebuildOwnership_Tests
    {
        [Theory]
        [InlineData("after-rebuild-source-claim", false)]
        [InlineData("after-rebuild-source-claim", true)]
        [InlineData("before-recovery-marker", false)]
        [InlineData("before-recovery-marker", true)]
        [InlineData("after-source-backup", false)]
        [InlineData("after-source-backup", true)]
        [InlineData("after-temp-install", false)]
        [InlineData("after-temp-install", true)]
        public void Source_and_candidate_cannot_be_opened_between_build_and_install(string phase, bool encrypted)
        {
            using var file = Seed(encrypted ? "password" : null);
            var password = encrypted ? "password" : null;
            var original = File.ReadAllBytes(file.Filename);
            var log = File.ReadAllBytes(FileHelper.GetLogFile(file.Filename));
            var reached = false;
            try
            {
                RebuildService.SimulateInstallFailure = RebuildService.SimulateOwnershipFailure = point =>
                {
                    if (point != phase) return;
                    reached = true;
                    foreach (var readOnly in new[] { false, true })
                    {
                        Action open = () => new LiteDatabase(new ConnectionString
                        {
                            Filename = file.Filename, Password = password, ReadOnly = readOnly
                        }).Dispose();
                        if (phase == "after-source-backup" || phase == "after-temp-install")
                            open.Should().Throw<LiteException>().Where(e => e.ErrorCode == LiteException.REBUILD_INCOMPLETE);
                        else open.Should().Throw<IOException>();
                    }
                    // Bypass admission/marker checks: the published candidate remains
                    // physically exclusive even to an opener with an old marker check.
                    if (phase == "after-temp-install")
                    {
                        Action rawOpen = () => new FileStream(file.Filename, FileMode.Open,
                            FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete).Dispose();
                        rawOpen.Should().Throw<IOException>();
                    }
                };
                new RebuildService(new EngineSettings { Filename = file.Filename, Password = password })
                    .Rebuild(new RebuildOptions());
            }
            finally { RebuildService.SimulateInstallFailure = RebuildService.SimulateOwnershipFailure = null; }
            reached.Should().BeTrue();
            File.ReadAllBytes(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().Equal(original);
            File.ReadAllBytes(FileHelper.GetSuffixFile(FileHelper.GetLogFile(file.Filename), "-backup", false)).Should().Equal(log);
            VerifyAndWrite(file.Filename, password);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void An_existing_data_or_WAL_handle_prevents_rebuild_without_changing_files(bool holdLog)
        {
            using var file = Seed(null);
            var data = File.ReadAllBytes(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            var log = File.ReadAllBytes(logName);
            var settings = new EngineSettings { Filename = file.Filename };
            using (var other = new FileStream(holdLog ? logName : file.Filename, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                for (var retry = 0; retry < 2; retry++)
                {
                    Action rebuild = () => new RebuildService(settings).Rebuild(new RebuildOptions());
                    rebuild.Should().Throw<IOException>();
                    File.ReadAllBytes(file.Filename).Should().Equal(data);
                    File.ReadAllBytes(logName).Should().Equal(log);
                    File.Exists(RebuildRecovery.GetMarkerFilename(file.Filename)).Should().BeFalse();
                    File.Exists(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().BeFalse();
                }
            }
            new RebuildService(settings).Rebuild(new RebuildOptions());
            VerifyAndWrite(file.Filename, null);
        }

        [Theory]
        [InlineData("after-rebuild-data-close")]
        [InlineData("after-rebuild-log-close")]
        [InlineData("after-rebuild-candidate-close")]
        public void Failed_claim_cleanup_preserves_installed_state_and_new_password(string phase)
        {
            using var file = Seed(null);
            var settings = new EngineSettings { Filename = file.Filename };
            using var engine = new LiteEngine(settings);
            var reached = false;
            try
            {
                RebuildService.SimulateInstallFailure = RebuildService.SimulateOwnershipFailure = point =>
                {
                    if (point != phase) return;
                    reached = true;
                    throw new IOException("claim cleanup failure");
                };
                Action rebuild = () => engine.Rebuild(new RebuildOptions { Password = "new-password" });
                rebuild.Should().Throw<AggregateException>().Which.Data[RebuildService.LiveStateDataKey]
                    .Should().Be(RebuildService.LiveStateReplacement);
            }
            finally { RebuildService.SimulateInstallFailure = RebuildService.SimulateOwnershipFailure = null; }
            reached.Should().BeTrue();
            settings.Password.Should().Be("new-password");
            VerifyAndWrite(file.Filename, settings.Password);
        }

        [Fact]
        public void Install_failure_remains_primary_when_claim_cleanup_also_fails()
        {
            using var file = Seed(null);
            try
            {
                RebuildService.SimulateInstallFailure = RebuildService.SimulateOwnershipFailure = point =>
                {
                    if (point == "after-temp-install") throw new IOException("installation failure");
                    if (point == "after-rebuild-data-close") throw new IOException("claim cleanup failure");
                };
                Action rebuild = () => new RebuildService(new EngineSettings { Filename = file.Filename })
                    .Rebuild(new RebuildOptions());
                var error = rebuild.Should().Throw<IOException>().WithMessage("installation failure").Which;
                error.Data[RebuildService.LiveStateDataKey].Should().Be(RebuildService.LiveStateOriginal);
                ((AggregateException)error.Data[RebuildService.RollbackErrorsDataKey]).Flatten().InnerExceptions
                    .Should().Contain(e => e.Message == "claim cleanup failure");
            }
            finally { RebuildService.SimulateInstallFailure = RebuildService.SimulateOwnershipFailure = null; }
            VerifyAndWrite(file.Filename, null);
        }

        internal static TempFile Seed(string password)
        {
            var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password }))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "original" });
                db.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "preserved" });
                db.Checkpoint();
                db.Pragma(Pragmas.CHECKPOINT, 0);
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "wal" });
            }
            return file;
        }

        [Fact]
        public void Readonly_connections_share_normally_outside_rebuild()
        {
            using var file = Seed(null);
            var data = File.ReadAllBytes(file.Filename);
            var log = File.ReadAllBytes(FileHelper.GetLogFile(file.Filename));
            using (var first = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true }))
            using (var second = new LiteDatabase(new ConnectionString
            {
                Filename = Path.Combine(Path.GetDirectoryName(file.Filename), ".", Path.GetFileName(file.Filename)),
                ReadOnly = true
            }))
            {
                first.GetCollection("rows").Count().Should().Be(2);
                second.GetCollection("rows").Count().Should().Be(2);
            }
            File.ReadAllBytes(file.Filename).Should().Equal(data);
            File.ReadAllBytes(FileHelper.GetLogFile(file.Filename)).Should().Equal(log);
        }

        [Fact]
        public void Caller_streams_are_not_claimed_closed_or_replaced()
        {
            using var file = Seed(null);
            var bytes = File.ReadAllBytes(file.Filename);
            using var data = new MemoryStream(bytes);
            using var log = new MemoryStream();
            Action rebuild = () => new RebuildService(new EngineSettings
            {
                Filename = file.Filename, DataStream = data, LogStream = log
            }).Rebuild(new RebuildOptions());
            rebuild.Should().Throw<NotSupportedException>().WithMessage("*engine-owned*");
            data.CanRead.Should().BeTrue();
            log.CanWrite.Should().BeTrue();
            data.ToArray().Should().Equal(bytes);
            File.ReadAllBytes(file.Filename).Should().Equal(bytes);
        }

        internal static void VerifyAndWrite(string filename, string password)
        {
            for (var retry = 0; retry < 2; retry++)
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = filename, Password = password });
                db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("original");
                db.GetCollection("rows").FindById(2)["value"].AsString.Should().Be("wal");
                db.GetCollection("unrelated").FindById(1)["value"].AsString.Should().Be("preserved");
                if (retry == 0) db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3, ["value"] = "new" });
                else db.GetCollection("rows").FindById(3)["value"].AsString.Should().Be("new");
            }
        }
    }
}
