using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Readable single-sequence companions to <see cref="Rebuild_InstallFaultMatrix_Tests"/>.
    /// </summary>
    public class Rebuild_InstallRollback_Tests
    {
        private static RebuildFaultRun Run(RebuildChange change, ConnectionType connection, params string[] faults)
        {
            var run = new RebuildFaultRun(new RebuildFaultScenario(change, true, connection), faults);
            run.Execute();
            run.Thrown.Should().Equal(faults, "every requested fault must have been reached");
            return run;
        }

        [Fact]
        public void Restoring_the_original_pair_removes_the_unpublished_replacement()
        {
            // The candidate of a password-removing rebuild is a plaintext copy of the whole database.
            using var run = Run(RebuildChange.RemovePassword, ConnectionType.Direct, "before-temp-install");

            run.LiveState.Should().Be(RebuildService.LiveStateOriginal);
            run.FilesAfter.Should().Equal(run.FilesBefore);
        }

        [Fact]
        public void Shared_handle_adopts_the_collation_of_a_republished_replacement()
        {
            using var run = Run(RebuildChange.Collation, ConnectionType.Shared, "after-temp-install", "before-source-rollback");

            run.LiveState.Should().Be(RebuildService.LiveStateReplacement);
            run.SameHandleRead.Should().Be(RebuildFaultRun.OldValue);
            run.ReadCopy(run.Live, null, false).Should().StartWith("THROWS:", "the live file now has the requested collation");
        }

        [Fact]
        public void Shared_handle_keeps_its_settings_when_the_original_pair_is_restored()
        {
            using var run = Run(RebuildChange.SetPassword, ConnectionType.Shared, "after-temp-install");

            run.LiveState.Should().Be(RebuildService.LiveStateOriginal);
            run.SameHandleRead.Should().Be(RebuildFaultRun.OldValue);
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Missing_live_file_is_reported_and_never_replaced_by_an_empty_database(ConnectionType connection)
        {
            using var run = Run(RebuildChange.SetPassword, connection,
                "before-temp-install", "before-source-rollback", "before-candidate-republish");

            run.LiveState.Should().Be(RebuildService.LiveStateIncomplete);
            run.Failure.Message.Should().Be("injected before-temp-install");
            run.RollbackErrors.Select(error => error.Message).Should().Contain(new[]
            {
                "injected before-source-rollback", "injected before-candidate-republish"
            });

            run.SameHandleRead.Should().StartWith("THROWS:");
            File.Exists(run.Live).Should().BeFalse("the handle must not create an empty database over the missing one");
            run.ReadCopy(run.Backup, run.BackupLog, false).Should().Be(RebuildFaultRun.OldValue);
            run.ReadCopy(run.Temp, null, true).Should().Be(RebuildFaultRun.OldValue);
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Original_data_stranded_without_its_wal_is_reported_and_never_served(ConnectionType connection)
        {
            using var run = Run(RebuildChange.SetPassword, connection,
                "before-temp-install", "before-log-rollback", "before-source-retraction");

            run.LiveState.Should().Be(RebuildService.LiveStateIncomplete);
            run.SameHandleRead.Should().StartWith("THROWS:");
            File.Exists(run.LiveLog).Should().BeFalse("the handle must not start a new WAL over the stranded one");
            run.ReadCopy(run.Live, run.BackupLog, false).Should().Be(RebuildFaultRun.OldValue, "the pair is still recombinable");
        }

        [Fact]
        public void Shared_handle_stays_refused_after_an_incomplete_rollback()
        {
            using var run = Run(RebuildChange.SetPassword, ConnectionType.Shared,
                "before-temp-install", "before-source-rollback", "before-candidate-republish");

            // Nothing about a later call can make the unresolved files safe to open.
            run.SameHandleRead.Should().Contain(run.Failure.Message);
        }

        [Fact]
        public void A_real_sharing_violation_on_the_source_restores_the_wal()
        {
            // Only Windows refuses to rename a file that is open without FileShare.Delete.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            using var file = new TempFile();
            using (var seed = new LiteDatabase(file.Filename))
            {
                seed.CheckpointSize = 0;
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "old" });
            }

            var logFile = FileHelper.GetLogFile(file.Filename);
            var tempFile = FileHelper.GetSuffixFile(file.Filename, "-temp", false);
            File.Exists(logFile).Should().BeTrue();

            var settings = new EngineSettings { Filename = file.Filename };
            Exception failure;
            using (File.Open(file.Filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                failure = Record.Exception(() => new RebuildService(settings).Rebuild(new RebuildOptions { Password = "new" }));
            }

            failure.Should().BeAssignableTo<IOException>();
            failure.Data[RebuildService.LiveStateDataKey].Should().Be(RebuildService.LiveStateOriginal);
            File.Exists(logFile).Should().BeTrue("the WAL moved aside before the failure must come back");
            File.Exists(tempFile).Should().BeFalse();

            using var recovered = new LiteDatabase(file.Filename);
            recovered.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("old");
        }
    }
}
