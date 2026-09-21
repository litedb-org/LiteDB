using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class RebuildRollbackMatrix_Tests
    {
        private static readonly string[] RollbackPhases =
        {
            "before-candidate-rollback", "before-source-rollback", "before-log-rollback",
            "before-source-retraction", "before-candidate-republish"
        };

        public static IEnumerable<object[]> Failures()
        {
            foreach (var phase in new[] { "after-log-backup", "after-source-backup", "after-temp-install" })
            foreach (var hasWal in new[] { false, true })
            for (var failures = 0; failures < 1 << RollbackPhases.Length; failures++)
                yield return new object[] { phase, hasWal, failures };
        }

        [Theory]
        [MemberData(nameof(Failures))]
        public void Every_rollback_combination_preserves_a_complete_database_or_blocks_access(string installPhase, bool hasWal, int failures)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-rollback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var filename = Path.Combine(directory, "data.db");
            var originalCollation = new Collation("en-US/None");
            var newCollation = new Collation("en-US/IgnoreCase");
            try
            {
                using (var seed = new LiteDatabase(new ConnectionString { Filename = filename, Collation = originalCollation }))
                {
                    seed.CheckpointSize = 0;
                    seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                    seed.Checkpoint();
                    seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                    if (!hasWal) seed.Checkpoint();
                }
                File.Exists(FileHelper.GetLogFile(filename)).Should().Be(hasWal);

                using var shared = new LiteDatabase(new ConnectionString
                {
                    Filename = filename, Connection = ConnectionType.Shared, Collation = originalCollation
                });
                RebuildService.SimulateInstallFailure = phase =>
                {
                    var index = Array.IndexOf(RollbackPhases, phase);
                    if (phase == installPhase || (index >= 0 && (failures & (1 << index)) != 0))
                        throw new IOException("injected " + phase);
                };
                Action rebuild = () => shared.Rebuild(new RebuildOptions { Password = "new", Collation = newCollation });
                var error = rebuild.Should().Throw<IOException>().WithMessage("injected " + installPhase).Which;
                RebuildService.SimulateInstallFailure = null;

                var replacement = error.Data[RebuildService.ReplacementPublishedDataKey] is true;
                var blocked = File.Exists(RebuildRecovery.GetMarkerFilename(filename));
                if (blocked)
                {
                    Action read = () => shared.GetCollection("rows").Count();
                    read.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
                    Action write = () => shared.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 });
                    write.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
                    Action fresh = () =>
                    {
                        using var db = new LiteDatabase(filename);
                        db.GetCollection("rows").Count();
                    };
                    fresh.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
                }
                else
                {
                    shared.GetCollection("rows").Count().Should().Be(2);
                    shared.Collation.ToString().Should().Be((replacement ? newCollation : originalCollation).ToString());
                    shared.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 });
                    shared.GetCollection("rows").Count().Should().Be(3);
                    using var fresh = new LiteDatabase(new ConnectionString
                    {
                        Filename = filename, Password = replacement ? "new" : null,
                        Collation = replacement ? newCollation : originalCollation
                    });
                    fresh.GetCollection("rows").Count().Should().Be(3);
                }

                // An unavailable canonical path must still preserve the full original history.
                if (blocked)
                {
                    var backup = FileHelper.GetSuffixFile(filename, "-backup", false);
                    var log = FileHelper.GetLogFile(filename);
                    var backupLog = FileHelper.GetSuffixFile(log, "-backup", false);
                    var recovered = Path.Combine(directory, "recovered.db");
                    File.Copy(File.Exists(backup) ? backup : filename, recovered);
                    if (hasWal) File.Copy(File.Exists(backupLog) ? backupLog : log, FileHelper.GetLogFile(recovered));
                    using var db = new LiteDatabase(recovered);
                    db.GetCollection("rows").Count().Should().Be(2);
                }
            }
            finally
            {
                RebuildService.SimulateInstallFailure = null;
                Directory.Delete(directory, true);
            }
        }
    }
}
