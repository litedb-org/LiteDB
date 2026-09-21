using System;
using System.IO;

using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2967_Tests
    {
        [Theory]
        [InlineData("after-log-backup")]
        [InlineData("after-source-backup")]
        [InlineData("after-temp-install")]
        public void Failed_password_rebuild_restores_the_original_data_and_wal_pair(string failurePhase)
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file.Filename))
            {
                seed.CheckpointSize = 0;
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "old" });
            }

            RebuildService.SimulateInstallFailure = phase =>
            {
                if (phase == failurePhase) throw new IOException("injected " + phase);
            };
            try
            {
                using var db = new LiteDatabase(file.Filename);
                Action rebuild = () => db.Rebuild(new RebuildOptions { Password = "new-password" });
                rebuild.Should().Throw<IOException>();
            }
            finally
            {
                RebuildService.SimulateInstallFailure = null;
            }

            using var recovered = new LiteDatabase(file.Filename);
            recovered.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("old");
        }

        [Fact]
        public void Failed_candidate_rollback_keeps_the_original_wal_with_its_backup_data()
        {
            using var file = new TempFile();
            using var recovery = new TempFile();
            using (var seed = new LiteDatabase(file.Filename))
            {
                seed.CheckpointSize = 0;
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "old" });
            }

            var logFile = FileHelper.GetLogFile(file.Filename);
            var backupFile = FileHelper.GetSuffixFile(file.Filename, "-backup", false);
            var backupLogFile = FileHelper.GetSuffixFile(logFile, "-backup", false);
            var recoveryLogFile = FileHelper.GetLogFile(recovery.Filename);
            File.Exists(logFile).Should().BeTrue();

            RebuildService.SimulateInstallFailure = phase =>
            {
                if (phase == "after-temp-install") throw new IOException("install failure");
                if (phase == "before-candidate-rollback") throw new IOException("candidate rollback failure");
            };
            try
            {
                IOException failure;
                using (var db = new LiteDatabase(file.Filename))
                {
                    failure = Record.Exception(() => db.Rebuild(new RebuildOptions { Password = "new-password" }))
                        .Should().BeOfType<IOException>().Which;
                }

                failure.Message.Should().Be("install failure");
                failure.Data["LiteDB.Rebuild.RollbackErrors"].Should().BeOfType<AggregateException>()
                    .Which.InnerExceptions.Should().HaveCount(2)
                    .And.Contain(error => error.Message == "candidate rollback failure");

                File.Exists(logFile).Should().BeFalse("the original data file was not restored");
                File.Exists(backupFile).Should().BeTrue();
                File.Exists(backupLogFile).Should().BeTrue();

                using (var replacement = new LiteDatabase(new ConnectionString
                {
                    Filename = file.Filename,
                    Password = "new-password"
                }))
                {
                    replacement.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("old");
                }

                File.Copy(backupFile, recovery.Filename);
                File.Copy(backupLogFile, recoveryLogFile);
                using var recovered = new LiteDatabase(recovery.Filename);
                recovered.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("old");
            }
            finally
            {
                RebuildService.SimulateInstallFailure = null;
                File.Delete(backupFile);
                File.Delete(backupLogFile);
                File.Delete(recoveryLogFile);
            }
        }

        [Fact]
        public void Failed_wal_rollback_republishes_candidate_and_preserves_original_pair()
        {
            using var file = new TempFile();
            using var recovery = new TempFile();
            using (var seed = new LiteDatabase(file.Filename))
            {
                seed.CheckpointSize = 0;
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "old" });
            }

            var logFile = FileHelper.GetLogFile(file.Filename);
            var backupFile = FileHelper.GetSuffixFile(file.Filename, "-backup", false);
            var backupLogFile = FileHelper.GetSuffixFile(logFile, "-backup", false);
            var recoveryLogFile = FileHelper.GetLogFile(recovery.Filename);
            File.Exists(logFile).Should().BeTrue();

            RebuildService.SimulateInstallFailure = phase =>
            {
                if (phase == "after-temp-install") throw new IOException("install failure");
                if (phase == "before-log-rollback") throw new IOException("WAL rollback failure");
            };
            try
            {
                IOException failure;
                using (var db = new LiteDatabase(file.Filename))
                {
                    failure = Record.Exception(() => db.Rebuild(new RebuildOptions { Password = "new-password" }))
                        .Should().BeOfType<IOException>().Which;
                }

                failure.Message.Should().Be("install failure");
                failure.Data["LiteDB.Rebuild.RollbackErrors"].Should().BeOfType<AggregateException>()
                    .Which.InnerExceptions.Should().ContainSingle()
                    .Which.Message.Should().Be("WAL rollback failure");

                File.Exists(logFile).Should().BeFalse("the live path contains the WAL-free replacement");
                File.Exists(backupFile).Should().BeTrue();
                File.Exists(backupLogFile).Should().BeTrue();

                using (var replacement = new LiteDatabase(new ConnectionString
                {
                    Filename = file.Filename,
                    Password = "new-password"
                }))
                {
                    replacement.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("old");
                }

                File.Copy(backupFile, recovery.Filename);
                File.Copy(backupLogFile, recoveryLogFile);
                using var recovered = new LiteDatabase(recovery.Filename);
                recovered.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("old");
            }
            finally
            {
                RebuildService.SimulateInstallFailure = null;
                File.Delete(backupFile);
                File.Delete(backupLogFile);
                File.Delete(recoveryLogFile);
            }
        }
    }
}
