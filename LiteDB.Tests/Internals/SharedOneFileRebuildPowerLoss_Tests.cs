#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using LiteDB.Tests.Engine;
using LiteDB.Utils;
using Xunit;
using Xunit.Abstractions;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileRebuildPowerLoss_Tests
    {
        private readonly ITestOutputHelper _output;
        public SharedOneFileRebuildPowerLoss_Tests(ITestOutputHelper output) => _output = output;

        [OneFileTheory]
        [InlineData(null, "forward")]
        [InlineData("secret", "forward")]
        [InlineData(null, "rollback")]
        [InlineData("secret", "rollback")]
        [InlineData(null, "retract")]
        [InlineData("secret", "retract")]
        [InlineData(null, "republish")]
        [InlineData("secret", "republish")]
        public void Every_durable_directory_image_and_repeated_recovery_preserves_committed_indexes(string password, string route)
            => CheckGraph(password, route, withoutWal: false);

        [OneFileTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Original_without_a_wal_also_recovers_every_directory_image(string password)
            => CheckGraph(password, "forward", withoutWal: true);

        private void CheckGraph(string password, string route, bool withoutWal)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            SeedSmallWal(files);
            if (withoutWal)
            {
                using (var db = new LiteDatabase(new ConnectionString { Filename = files.Filename, Password = password })) db.Checkpoint();
                File.Delete(files.Log);
            }
            using var device = new RebuildDirectoryCrashDevice(files.Filename);
            RebuildDirectoryCrashDevice.Image beforeCleanup = null;
            var reached = new HashSet<string>();
            MappedRebuildRecovery.MappedRebuildStage = stage =>
            {
                reached.Add("guard:" + stage);
                if (stage == "recovery-log-restored" || (withoutWal && stage == "recovery-source-restored")) beforeCleanup = device.Read();
                device.Stage = "guard:" + stage;
                if (stage == "before-recovery-marker") device.Start();
            };
            RebuildService.SimulateInstallFailure = stage =>
            {
                device.Stage = "install:" + stage;
                if (route != "forward" && (stage == "after-temp-install" ||
                    (route == "retract" && stage == "before-log-rollback") ||
                    (route == "republish" && stage == "before-source-rollback")))
                    throw new IOException("Drive " + route);
            };
            try
            {
                var error = Record.Exception(() => new RebuildService(files.Settings).Rebuild(new RebuildOptions()));
                if (route == "forward") error.Should().BeNull();
                else error.Should().BeOfType<IOException>().Which.Message.Should().Be("Drive " + route);
                RebuildService.SimulateInstallFailure = null;
                device.Armed = false;
                device.Barriers.Should().BeGreaterThanOrEqualTo(withoutWal ? 6 : 7);
                foreach (var stage in new[] { "guard:marker-file-synced", "guard:marker-published",
                    "install:before-source-backup", "install:before-temp-install",
                    "install:before-recovery-marker-delete" })
                    device.Transitions.Should().Contain(stage);
                if (!withoutWal) device.Transitions.Should().Contain("install:before-log-backup");
                if (route != "forward") device.Transitions.Should().Contain("install:before-candidate-rollback");
                if (route == "rollback") device.Transitions.Should().Contain(new[]
                    { "install:before-source-rollback", "install:before-log-rollback" });
                if (route == "retract") device.Transitions.Should().Contain(new[]
                    { "install:before-source-retraction", "install:before-candidate-republish" });
                if (route == "republish") device.Transitions.Should().Contain("install:before-candidate-republish");

                // Work to a fixed point, rather than choosing one additional cut:
                // every crash image emitted by recovery is recovered and cut again.
                // This includes arbitrary repetitions/cycles of interrupted recovery.
                for (var index = 0; index < device.Images.Count; index++)
                {
                    device.Images.Count.Should().BeLessThan(100, "recovery must converge to a finite namespace state graph");
                    var image = device.Images[index];
                    device.Restore(image);
                    device.Resume();
                    RebuildRecovery.EnsureAvailable(files.Settings);
                    device.Armed = false;
                    // Also permit the independent candidate/guard deletions to
                    // reorder across their intervening barrier, a strictly stronger
                    // cleanup model than successful directory sync requires.
                    if (beforeCleanup != null)
                    {
                        device.IncludeReordering(beforeCleanup, device.Read());
                        beforeCleanup = null;
                    }
                    RebuildRecovery.EnsureAvailable(files.Settings);
                    File.Exists(RebuildRecovery.GetMarkerFilename(files.Filename)).Should().BeFalse(image.Event);
                    VerifySmallTwice(files);
                }
                reached.Should().Contain(new[] { "guard:recovery-exclusive",
                    "guard:recovery-candidate-retracted", "guard:recovery-source-restored",
                    "guard:recovery-candidate-cleaned", "guard:recovery-verified", "guard:recovery-complete" });
                if (!withoutWal) reached.Should().Contain("guard:recovery-log-restored");
                device.ReorderedStates.Should().BeGreaterThan(0, "candidate cleanup and guard deletion may persist independently");
                _output.WriteLine($"route={route}; states={device.Images.Count}; barriers={device.Barriers}; reordered={device.ReorderedStates}");
                device.Images.Count.Should().Be(withoutWal ? 8 : route == "rollback" ? 8 : 9, "the complete modeled namespace graph must be visited");
            }
            finally
            {
                MappedRebuildRecovery.MappedRebuildStage = null;
                RebuildService.SimulateInstallFailure = null;
            }
        }

        [OneFileTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Every_pending_write_prefix_can_be_lost_and_torn_guard_bytes_remain_blocked(string password)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            SeedSmallWal(files);
            using var device = new RebuildDirectoryCrashDevice(files.Filename);
            RebuildDirectoryCrashDevice.Image pending = null, guarded = null;
            MappedRebuildRecovery.MappedRebuildStage = stage =>
            {
                if (stage == "before-recovery-marker") device.Start();
                if (stage == "marker-durable") guarded = device.Read();
            };
            try { new RebuildService(files.Settings).Rebuild(new RebuildOptions()); }
            finally { MappedRebuildRecovery.MappedRebuildStage = null; device.Armed = false; }
            var marker = RebuildRecovery.GetMarkerFilename(files.Filename);
            pending = device.Images.Single(image => image.Names.ContainsKey(marker + ".pending"));
            pending.Should().NotBeNull();
            guarded.Should().NotBeNull();
            // Tear the actual physical record, including the full OS identity
            // encodings, rather than its shorter abstract inode-token encoding.
            device.Restore(pending);
            var bytes = File.ReadAllBytes(marker + ".pending");
            // Extend the existing byte crash device: all truncation/tear prefixes
            // of an unsynced pending write, including zero and a complete write.
            // A guard cannot tear after successful file sync; independently test
            // damaged published guard bytes as corrupt input, preserving evidence.
            for (var prefix = 0; prefix <= bytes.Length; prefix++)
            {
                using var stream = new PromotionPowerLossStream();
                stream.TearWrite(bytes, 0, bytes.Length, prefix, damage: false, shortFile: true);
                var torn = stream.DurableBytes;
                device.Restore(pending);
                File.WriteAllBytes(marker + ".pending", torn);
                VerifySmallTwice(files);
                if (prefix == bytes.Length) continue;
                device.Restore(guarded);
                File.WriteAllBytes(marker, torn);
                SharedOneFileRebuildGuardInput_Tests.RefuseWithoutMutation(files.Settings);
            }
            _output.WriteLine($"pending prefixes={bytes.Length + 1}; damaged guard prefixes={bytes.Length}; each recovery/refusal repeated twice");
            device.Restore(guarded);
            RebuildRecovery.EnsureAvailable(files.Settings);
            VerifySmallTwice(files);
        }

        [OneFileFact]
        public void Committed_oracle_detects_a_lost_guard_with_a_stranded_acknowledged_wal()
        {
            using var files = new SharedOneFilePublicationFixture();
            SeedSmallWal(files);
            var stranded = files.Log + ".stranded";
            File.Move(files.Log, stranded);
            try
            {
                Action lostCommit = () => VerifySmallTwice(files);
                lostCommit.Should().Throw<Xunit.Sdk.XunitException>("a missing guard must never admit stale original data without its WAL");
            }
            finally
            {
                if (File.Exists(files.Log)) File.Delete(files.Log);
                File.Move(stranded, files.Log);
            }
            VerifySmallTwice(files);
        }

        [OneFileTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Every_full_length_guard_tear_preserves_the_guarded_pair(string password)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            await SharedOneFileRebuildGuardInput_Tests.Interrupt(files);
            var marker = RebuildRecovery.GetMarkerFilename(files.Filename);
            var valid = File.ReadAllBytes(marker);
            for (var prefix = 0; prefix < valid.Length; prefix++)
            {
                // Length metadata can survive while only a prefix of the bytes
                // reaches media. Test this separately from short-file tears.
                using var stream = new PromotionPowerLossStream();
                stream.TearWrite(valid, 0, valid.Length, prefix, damage: true);
                stream.DurableBytes.Length.Should().Be(valid.Length);
                var torn = stream.DurableBytes;
                // An already-zero suffix is the unchanged positive-control image.
                if (torn.SequenceEqual(valid)) continue;
                File.WriteAllBytes(marker, torn);
                SharedOneFileRebuildGuardInput_Tests.RefuseWithoutMutation(files.Settings);
            }
            File.WriteAllBytes(marker, valid);
            SharedOneFileMaintenanceCrash_Tests.Recover(files);
            _output.WriteLine($"full-length zero-tail guard tears={valid.Length}; each refusal repeated twice");
        }

        private static void SeedSmallWal(SharedOneFilePublicationFixture files)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = files.Filename, Password = files.Settings.Password });
            db.GetCollection("docs").DeleteMany(Query.GT("_id", 7));
            db.Checkpoint();
            db.CheckpointSize = 0;
            db.GetCollection("docs").Update(Row(1, 11));
        }

        private static void VerifySmallTwice(SharedOneFilePublicationFixture files)
        {
            var expected = Enumerable.Range(1, 7).Select(id => Row(id, id == 1 ? 11 : 0)).ToArray();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = files.Filename, Password = files.Settings.Password });
                db.GetCollection("docs").Query().Where(Query.EQ("value", 11)).GetPlan()["index"]["name"].AsString.Should().Be("value");
                db.GetCollection("sentinel").Query().Where(Query.EQ("value", 9)).GetPlan()["index"]["name"].AsString.Should().Be("value");
                db.GetCollection("docs").FindAll().Should().BeEquivalentTo(expected);
                db.GetCollection("docs").Find(Query.EQ("value", 11)).Should().BeEquivalentTo(new[] { Row(1, 11) });
                db.GetCollection("docs").Find(Query.EQ("value", 0)).Should().BeEquivalentTo(expected.Skip(1));
                db.GetCollection("sentinel").FindAll().Should().BeEquivalentTo(new[] { Row(42, 9) });
                db.GetCollection("sentinel").Find(Query.EQ("value", 9)).Should().BeEquivalentTo(new[] { Row(42, 9) });
            }
        }

        internal static void SeedAcknowledgedWal(SharedOneFilePublicationFixture files)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = files.Filename, Password = files.Settings.Password });
            db.CheckpointSize = 0;
            db.GetCollection("docs").Update(Row(1, 11));
            Verify(db, 11);
        }

        internal static void VerifyTwice(SharedOneFilePublicationFixture files)
        {
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = files.Filename, Password = files.Settings.Password });
                Verify(db, 11);
            }
        }
    }
}
#endif
