using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2979_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-2979-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "data.db");
        private string Backup => FileHelper.GetSuffixFile(Filename, "-backup", false);
        private string BackupLog => FileHelper.GetSuffixFile(FileHelper.GetLogFile(Filename), "-backup", false);
        private string Candidate => FileHelper.GetSuffixFile(Filename, "-temp", false);

        public Issue2979_Tests()
        {
            Directory.CreateDirectory(_directory);
        }

        [Theory]
        [InlineData("before-source-retraction", false)]
        [InlineData("before-source-retraction", true)]
        [InlineData("before-candidate-republish", false)]
        [InlineData("before-candidate-republish", true)]
        public void Repeated_recovery_failures_block_shared_reuse_and_fresh_opens(string recoveryFailure, bool encrypted)
        {
            var password = encrypted ? "old-password" : null;
            Seed(password);
            using var db = new LiteDatabase(new ConnectionString
            {
                Filename = Filename, Password = password, Connection = ConnectionType.Shared
            });
            RebuildService.SimulateInstallFailure = phase =>
            {
                if (phase == "after-temp-install" || phase == "before-log-rollback" || phase == recoveryFailure)
                    throw new IOException("injected " + phase);
            };

            Action rebuild = () => db.Rebuild(new RebuildOptions { Password = "new-password" });
            var error = rebuild.Should().Throw<IOException>().WithMessage("injected after-temp-install").Which;
            error.Data["LiteDB.Rebuild.RollbackErrors"].Should().BeOfType<AggregateException>()
                .Which.InnerExceptions.Should().HaveCount(2);
            RebuildService.SimulateInstallFailure = null;

            var sourceIsLive = recoveryFailure == "before-source-retraction";
            File.Exists(Filename).Should().Be(sourceIsLive);
            File.Exists(FileHelper.GetLogFile(Filename)).Should().BeFalse();

            Action reuse = () => db.GetCollection("rows").Count();
            reuse.Should().Throw<LiteException>().WithMessage("*recovery is incomplete*");
            Action write = () => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 });
            write.Should().Throw<LiteException>().WithMessage("*recovery is incomplete*");

            foreach (var connection in new[] { ConnectionType.Direct, ConnectionType.Shared })
            foreach (var readOnly in new[] { false, true })
            {
                Action reopen = () =>
                {
                    using var fresh = new LiteDatabase(new ConnectionString
                    {
                        Filename = Filename, Password = password, Connection = connection,
                        ReadOnly = readOnly, Upgrade = true, AutoRebuild = true
                    });
                    fresh.GetCollection("rows").Count();
                };
                reopen.Should().Throw<LiteException>().WithMessage("*recovery is incomplete*");
            }

            // Failed opens must not create a fresh data file or an unrelated WAL.
            File.Exists(Filename).Should().Be(sourceIsLive);
            File.Exists(FileHelper.GetLogFile(Filename)).Should().BeFalse();

            // Both histories remain recoverable away from the guarded canonical path.
            var recoveredPath = Path.Combine(_directory, "recovered.db");
            File.Copy(sourceIsLive ? Filename : Backup, recoveredPath);
            File.Copy(BackupLog, FileHelper.GetLogFile(recoveredPath));
            using (var recovered = new LiteDatabase(new ConnectionString { Filename = recoveredPath, Password = password }))
            {
                AssertRows(recovered);
            }
            using var candidate = new LiteDatabase(new ConnectionString { Filename = Candidate, Password = "new-password" });
            AssertRows(candidate);
        }

        private void Seed(string password)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = Filename, Password = password });
            db.CheckpointSize = 0;
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "checkpointed" });
            db.Checkpoint();
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "WAL-only" });
        }

        private static void AssertRows(LiteDatabase db)
        {
            db.GetCollection("rows").Count().Should().Be(2);
            db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("checkpointed");
            db.GetCollection("rows").FindById(2)["value"].AsString.Should().Be("WAL-only");
        }

        public void Dispose()
        {
            RebuildService.SimulateInstallFailure = null;
            Directory.Delete(_directory, true);
        }
    }
}
