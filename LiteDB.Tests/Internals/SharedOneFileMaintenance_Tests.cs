#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileMaintenance_Tests
    {
        [OneFileTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Alone_rebuild_rekeys_mapping_and_preserves_complete_indexed_state(string password)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            Guid replacement;
            using (var engine = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("docs").Update(Row(1, 11));
                var table = engine.MappedReaderTable;
                var original = table.Authority.Header.Authority;
                var identity = table.Admission.Identity;
                var registryKey = table.Admission.RegistryKey;
                var namespaceCuts = 0;
                SharedMemoryMaintenance.MaintenanceStage = stage =>
                {
                    if (stage != "rekey-invalidated" && stage != "rekey-envelope-a" && stage != "rekey-envelope-b") return;
                    namespaceCuts++;
                    SharedPlatformWake_Tests.AssertDarwinNameExists(original, false);
                    if (stage != "rekey-invalidated")
                        SharedPlatformWake_Tests.AssertNativeNameExists(table.Authority.Header.Authority, false);
                };
                try { db.Rebuild(); }
                finally { SharedMemoryMaintenance.MaintenanceStage = null; }
                namespaceCuts.Should().Be(3);
                replacement = table.Authority.Header.Authority;
                replacement.Should().NotBe(original);
                SharedPlatformWake_Tests.AssertNativeNameExists(original, false);
                SharedPlatformWake_Tests.AssertNativeNameExists(replacement, true);
                table.Admission.Identity.Should().NotBe(identity);
                table.Admission.RegistryKey.Should().BeSameAs(registryKey);
                table.Authority.Header.MatchesIdentity(table.Admission.Identity).Should().BeTrue();
                Verify(db, 11);
                db.Checkpoint();
                db.GetCollection("docs").Update(Row(1, 12));
                Verify(db, 12);
                // A subsequent facade must attach to this same participant, even
                // though its canonical database now has a different physical inode.
                using var sibling = new SharedEngine(files.Settings);
                sibling.MappedReaderTable.Should().BeSameAs(table);
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var engine = new SharedEngine(files.Settings);
                engine.MappedReaderTable.Authority.Header.Authority.Should().Be(replacement);
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                Verify(db, 12);
            }
        }

        [OneFileFact]
        public async Task Remote_peer_excludes_maintenance_until_detach_even_for_retained_tenure()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            db.GetCollection("docs").Update(Row(1, 0)); // establish tenure
            using (var peer = new MvccProcess("one-file-maintenance-peer", files.Filename, null))
            {
                await peer.Expect("ready");
                Action rebuild = () => db.Rebuild();
                rebuild.Should().Throw<InvalidOperationException>().WithMessage("*only participant*");
                db.GetCollection("docs").Update(Row(1, 11));
                db.Checkpoint(); // partial backfill must remain available with peers
                new FileInfo(FileHelper.GetLogFile(files.Filename)).Length.Should().BeGreaterThan(0);
                Verify(db, 11);
                await peer.Finish(release: true);
            }
            db.GetCollection("docs").Update(Row(1, 11));
            var retained = Core(engine);
            db.Rebuild();
            retained.Should().NotBeNull();
            Verify(db, 11);
        }

        [OneFileTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Checkpoint_backfills_with_an_idle_process_and_truncates_only_after_detach(string password)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            using (var peer = new MvccProcess("one-file-maintenance-idle", files.Filename, password))
            {
                await peer.Expect("ready");
                db.GetCollection("docs").Update(Row(1, 11));
                engine.MappedReaderTable.LiveVersions().Should().BeEmpty();
                engine.Checkpoint().Should().BeGreaterThan(0);
                new FileInfo(files.Log).Length.Should().BeGreaterThan(0);
                Verify(db, 11);
                await peer.Finish(release: true);
            }
            db.Checkpoint();
            new FileInfo(files.Log).Length.Should().Be(password == null ? 0 : Constants.PAGE_SIZE);
            Verify(db, 11);
        }

        [OneFileFact]
        public void Local_facade_or_retained_generation_excludes_replacement()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using (var sibling = new SharedEngine(files.Settings))
            {
                Action rebuild = () => db.Rebuild();
                rebuild.Should().Throw<InvalidOperationException>().WithMessage("*only participant*");
            }
            using (var reader = engine.Query("docs", new Query()))
            {
                reader.Read().Should().BeTrue();
                Action rebuild = () => db.Rebuild();
                rebuild.Should().Throw<InvalidOperationException>().WithMessage("*only participant*");
                var count = 1;
                while (reader.Read()) count++;
                count.Should().Be(160);
            }
            Verify(db, 0);
            db.Rebuild();
            Verify(db, 0);
        }
    }
}
#endif
