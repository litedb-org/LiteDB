#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using LiteDB.Utils;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileMaintenanceFailure_Tests
    {
        [OneFileFact]
        public void Rebuild_refuses_a_failed_storage_close_before_creating_a_replacement()
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                engine.PinIdleLimit = engine.PinHoldLimit = TimeSpan.FromMinutes(1);
                db.CheckpointSize = 1000;
                for (var write = 0; write < 80; write++) db.GetCollection("docs").Update(Row(1, 11));
                new FileInfo(files.Log).Length.Should().BeGreaterThan(50 * Constants.PAGE_SIZE);
                var reached = false;
                EngineState.SimulateProcessCrash = stage =>
                { if (stage == "checkpoint-before-data-flush") { reached = true; throw new IOException("rebuild close failed"); } };
                try
                {
                    Action rebuild = () => db.Rebuild();
                    rebuild.Should().Throw<AggregateException>().WithMessage("Mapped rebuild refused*");
                    reached.Should().BeTrue();
                    File.Exists(RebuildRecovery.GetMarkerFilename(files.Filename)).Should().BeFalse();
                    Directory.GetFiles(Path.GetDirectoryName(files.Filename), "*backup*").Should().BeEmpty();
                }
                finally { EngineState.SimulateProcessCrash = null; }
            }
            SharedOneFileMaintenanceCrash_Tests.Recover(files);
        }
        [OneFileTheory]
        [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
        [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
        public void Each_failed_directory_sync_preserves_a_recoverable_pair(int failure)
        {
            using var files = new SharedOneFilePublicationFixture("secret");
            var armed = false;
            var count = 0;
            var hit = false;
            using (var engine = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("docs").Update(Row(1, 11));
                MappedRebuildRecovery.MappedRebuildStage = stage =>
                { if (stage == "before-recovery-marker") armed = true; };
                NativeFileSync.SimulateDirectoryErrno = directory =>
                {
                    if (armed && failure != 8 && ++count == failure) { hit = true; return 5; }
                    return 0;
                };
                NativeFileSync.SimulateErrno = filename =>
                {
                    if (failure == 8 && armed && filename.EndsWith(".pending")) { hit = true; return 5; }
                    return 0;
                };
                try
                {
                    Action rebuild = () => db.Rebuild();
                    rebuild.Should().Throw<Exception>();
                    hit.Should().BeTrue("the selected durable boundary must execute");
                }
                finally
                {
                    NativeFileSync.SimulateDirectoryErrno = null;
                    NativeFileSync.SimulateErrno = null;
                    MappedRebuildRecovery.MappedRebuildStage = null;
                }
            }
            SharedOneFileMaintenanceCrash_Tests.Recover(files);
        }

        [OneFileFact]
        public async Task Damaged_or_partial_recovery_record_blocks_without_mutating_the_pair()
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var install = new MvccProcess("one-file-maintenance-cut", files.Filename, null, "install:after-temp-install"))
            {
                await install.Expect("ready");
                await install.Kill();
            }
            var marker = RebuildRecovery.GetMarkerFilename(files.Filename);
            var valid = File.ReadAllBytes(marker);
            var data = File.ReadAllBytes(files.Filename);
            foreach (var corrupt in new[] { Array.Empty<byte>(), new byte[] { 1 }, Corrupt(valid) })
            {
                File.WriteAllBytes(marker, corrupt);
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    Action open = () => { using var engine = new SharedEngine(files.Settings); };
                    open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
                    File.ReadAllBytes(marker).Should().Equal(corrupt);
                    File.ReadAllBytes(files.Filename).Should().Equal(data);
                }
            }
            File.WriteAllBytes(marker, valid);
            SharedOneFileMaintenanceCrash_Tests.Recover(files);
        }
        private static byte[] Corrupt(byte[] valid)
        {
            var corrupt = (byte[])valid.Clone();
            corrupt[corrupt.Length / 2] ^= 0x40;
            return corrupt;
        }

        [OneFileFact]
        public void Password_replacement_updates_retained_settings_and_new_local_facades()
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("docs").Update(Row(1, 11));
                db.Rebuild(new RebuildOptions { Password = "new-secret" });
                Verify(db, 11);
                var settings = files.Settings;
                settings.Password = "new-secret";
                using var sibling = new SharedEngine(settings);
                sibling.MappedReaderTable.Should().BeSameAs(engine.MappedReaderTable);
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = files.Filename, Password = "new-secret" });
                Verify(db, 11);
            }
        }
    }
}
#endif
