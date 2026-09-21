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
        public void Rollback_failure_does_not_mask_install_failure_or_skip_later_steps()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file.Filename))
            {
                seed.CheckpointSize = 0;
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            }
            var logFile = FileHelper.GetLogFile(file.Filename);
            File.Exists(logFile).Should().BeTrue();

            RebuildService.SimulateInstallFailure = phase =>
            {
                if (phase == "after-temp-install") throw new IOException("install failure");
                if (phase == "before-source-rollback") throw new IOException("rollback failure");
            };
            IOException failure;
            try
            {
                using var db = new LiteDatabase(file.Filename);
                failure = Record.Exception(() => db.Rebuild(new RebuildOptions { Password = "new-password" }))
                    .Should().BeOfType<IOException>().Which;
            }
            finally
            {
                RebuildService.SimulateInstallFailure = null;
            }

            failure.Message.Should().Be("install failure");
            failure.Data["LiteDB.Rebuild.RollbackErrors"].Should().BeOfType<AggregateException>()
                .Which.InnerExceptions.Should().Contain(error => error.Message == "rollback failure");
            File.Exists(logFile).Should().BeTrue("WAL restoration must still be attempted after another rollback step fails");
        }
    }
}
