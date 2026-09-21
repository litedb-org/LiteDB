using System;
using System.IO;
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

        [Fact]
        public void A_failed_build_leaves_no_partial_replacement_behind()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = "secret" }))
            {
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "old" });
            }

            // The replacement is built through its own WAL. A directory in that place lets the build
            // create the replacement file and then fail, with no injected fault involved.
            var temp = FileHelper.GetSuffixFile(file.Filename, "-temp", false);
            var tempLog = FileHelper.GetLogFile(temp);
            Directory.CreateDirectory(tempLog);
            try
            {
                var settings = new EngineSettings { Filename = file.Filename, Password = "secret" };
                var failure = Record.Exception(() => new RebuildService(settings).Rebuild(new RebuildOptions { RemovePassword = true }));

                failure.Should().NotBeNull();
                File.Exists(temp).Should().BeFalse("it would be a partial, unencrypted copy of the database");
                File.Exists(RebuildRecovery.GetMarkerFilename(file.Filename)).Should().BeFalse();

                using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = "secret" });
                db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("old");
            }
            finally
            {
                Directory.Delete(tempLog, true);
            }
        }

        [Fact]
        public void Wal_probe_reports_absence_only_when_the_file_is_known_to_be_absent()
        {
            using var file = new TempFile();
            File.WriteAllText(file.Filename, "present");
            var missingDirectory = Path.Combine(Path.GetDirectoryName(file.Filename), Guid.NewGuid().ToString("n"), "data-log.db");

            FileHelper.ExistsOrThrow(file.Filename).Should().BeTrue();
            FileHelper.ExistsOrThrow(file.Filename + ".missing").Should().BeFalse();
            FileHelper.ExistsOrThrow(missingDirectory).Should().BeFalse();

            // File.Exists answers "false" to a probe it could not perform. Installing a replacement
            // on that answer would leave an unseen original WAL live beside it.
            var unprobeable = file.Filename + "\0";
            File.Exists(unprobeable).Should().BeFalse();
            Action probe = () => FileHelper.ExistsOrThrow(unprobeable);
            probe.Should().Throw<Exception>();
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
