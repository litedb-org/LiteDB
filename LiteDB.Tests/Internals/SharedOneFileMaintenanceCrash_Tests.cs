#if NET8_0_OR_GREATER
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileMaintenanceCrash_Tests
    {
        internal static readonly string[] Cuts = {
            "maintenance:exclusive", "maintenance:fenced", "maintenance:before-rekey",
            "maintenance:rekey-invalidated", "maintenance:rekey-envelope-a", "maintenance:rekey-envelope-b",
            "maintenance:rekeyed", "maintenance:published",
            "recovery:before-recovery-marker", "recovery:before-recovery-marker-flush", "recovery:marker-written",
            "recovery:marker-file-synced", "recovery:marker-published", "recovery:marker-durable",
            "install:before-log-backup", "install:after-log-backup", "install:before-source-backup",
            "install:after-source-backup", "install:before-temp-install", "install:after-temp-install",
            "install:before-recovery-marker-delete" };
        public static IEnumerable<object[]> Boundaries => Cuts.SelectMany(cut => new[] {
            new object[] { null, cut }, new object[] { "secret", cut } });
        [OneFileTheory]
        [MemberData(nameof(Boundaries))]
        public async Task Every_maintenance_death_preserves_committed_pair_and_repeats(string password, string cut)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var child = new MvccProcess("one-file-maintenance-cut", files.Filename, password, cut);
                await child.Expect("ready");
                await child.Kill();
                Recover(files);
            }
        }
        public static IEnumerable<object[]> RecoveryBoundaries => new[] {
            "recovery-exclusive", "recovery-candidate-retracted", "recovery-source-restored",
            "recovery-log-restored", "recovery-candidate-cleaned", "recovery-verified", "recovery-complete"
        }.SelectMany(cut => new[] { new object[] { null, cut }, new object[] { "secret", cut } });
        [OneFileTheory]
        [MemberData(nameof(RecoveryBoundaries))]
        public async Task Recovery_can_itself_die_and_be_repeated(string password, string cut)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using (var install = new MvccProcess("one-file-maintenance-cut", files.Filename, password, "install:after-temp-install"))
                {
                    await install.Expect("ready");
                    await install.Kill();
                }
                using (var recovery = new MvccProcess("one-file-recovery-cut", files.Filename, password, "recovery:" + cut))
                {
                    await recovery.Expect("ready");
                    await recovery.Kill();
                }
                Recover(files);
            }
        }
        public static IEnumerable<object[]> RollbackBoundaries => new[] { "before-candidate-rollback", "before-source-rollback",
            "before-log-rollback", "before-source-retraction", "before-candidate-republish", "before-candidate-cleanup" }
            .SelectMany(cut => new[] { new object[] { null, cut }, new object[] { "secret", cut } });
        [OneFileTheory]
        [MemberData(nameof(RollbackBoundaries))]
        public async Task Death_during_bounded_rollback_keeps_the_original_pair_recoverable(string password, string cut)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var child = new MvccProcess("one-file-maintenance-rollback-cut", files.Filename, password, "install:" + cut);
                await child.Expect("ready");
                await child.Kill();
                Recover(files);
            }
        }
        internal static void Recover(SharedOneFilePublicationFixture files)
        {
            for (var cold = 0; cold < 2; cold++)
            {
                using var engine = new SharedEngine(files.Settings);
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                Verify(db, 11);
                File.Exists(RebuildRecovery.GetMarkerFilename(files.Filename)).Should().BeFalse();
                db.Checkpoint();
            }
            using var direct = new LiteDatabase(new ConnectionString
                { Filename = files.Filename, Password = files.Settings.Password });
            Verify(direct, 11);
        }
    }
}
#endif
