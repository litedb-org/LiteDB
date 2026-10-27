#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using LiteDB.Tests.Engine;
using Xunit;
using static LiteDB.Constants;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileMaintenanceOpening_Tests
    {
        [OneFileTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public void AutoRebuild_alone_rekeys_and_is_repeatable(string password)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            Guid old;
            using (var first = new SharedEngine(files.Settings)) old = first.MappedReaderTable.Authority.Header.Authority;
            MarkInvalid(files);
            var settings = files.Settings;
            settings.AutoRebuild = true;
            Guid authority;
            var checkedExclusive = false;
            SharedMemoryAuthority.BootstrapStage = stage =>
            {
                if (stage != "after-storage") return;
                using var probe = new DatabaseFileLock(files.Filename + "-shm", false, false);
                probe.TryLock(SharedMemoryLayout.Dms, false).Should().BeFalse(
                    "nested maintenance must retain bootstrap's exclusive DMS until initialization finishes");
                checkedExclusive = true;
            };
            try
            {
                using var engine = new SharedEngine(settings);
                authority = engine.MappedReaderTable.Authority.Header.Authority;
                authority.Should().NotBe(old);
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                Verify(db, 0);
            }
            finally { SharedMemoryAuthority.BootstrapStage = null; }
            checkedExclusive.Should().BeTrue();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var engine = new SharedEngine(settings);
                engine.MappedReaderTable.Authority.Header.Authority.Should().Be(authority);
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                Verify(db, 0);
            }
        }
        [OneFileTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Legacy_WAL_conversion_requires_sole_DMS_even_with_no_active_snapshot(string password)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            using (var data = ChecksumTestFiles.Copy(File.ReadAllBytes(files.Filename)))
            using (var log = new MemoryStream())
            {
                ChecksumTestFiles.MakeLegacy(data, log, password);
                File.WriteAllBytes(files.Filename, data.ToArray());
            }
            var before = File.ReadAllBytes(files.Filename);
            using (var peer = new MvccProcess("one-file-maintenance-legacy-reader", files.Filename, password))
            {
                await peer.Expect("ready");
                Action open = () => { using var engine = new SharedEngine(files.Settings); };
                open.Should().Throw<LiteException>().WithMessage("*another mapped process*");
                File.ReadAllBytes(files.Filename).Should().Equal(before);
                await peer.Finish(release: true);
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var engine = new SharedEngine(files.Settings);
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                Verify(db, 0);
            }
        }
        [OneFileFact]
        public async Task AutoRebuild_with_remote_peer_refuses_before_replacement_and_keeps_bytes()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var peer = new MvccProcess("one-file-maintenance-peer", files.Filename, null);
            await peer.Expect("ready");
            MarkInvalid(files);
            var data = File.ReadAllBytes(files.Filename);
            var log = File.Exists(files.Log) ? File.ReadAllBytes(files.Log) : Array.Empty<byte>();
            var settings = files.Settings;
            settings.AutoRebuild = true;
            Action open = () => { using var engine = new SharedEngine(settings); };
            open.Should().Throw<InvalidOperationException>().WithMessage("*only participant*");
            File.ReadAllBytes(files.Filename).Should().Equal(data);
            (File.Exists(files.Log) ? File.ReadAllBytes(files.Log) : Array.Empty<byte>()).Should().Equal(log);
            await peer.Finish(release: true);
            using var recovered = new SharedEngine(settings);
            using var db = new LiteDatabase(recovered, disposeOnClose: false);
            Verify(db, 0);
        }
        [OneFileTheory]
        [InlineData("v4.db", null)]
        [InlineData("Issue_2494_EncryptedV4.db", "pass123")]
        public void File_replacing_v7_upgrade_runs_offline_and_reopens_twice(string resource, string password)
        {
            using var files = new SharedOneFilePublicationFixture();
            File.Copy("../../../Resources/" + resource, files.Filename, overwrite: true);
            var before = File.ReadAllBytes(files.Filename);
            var settings = files.Settings;
            settings.Password = password;
            settings.Upgrade = true;
            BsonDocument[][] expected;
            string[] names;
            using (var engine = new SharedEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                names = db.GetCollectionNames().OrderBy(n => n).ToArray();
                names.Should().NotBeEmpty();
                expected = names.Select(n => db.GetCollection(n).FindAll().ToArray()).ToArray();
                if (password == null) db.GetCollection("col1").Count().Should().Be(3);
                engine.MappedReaderTable.Authority.Header.MatchesIdentity(engine.MappedReaderTable.Admission.Identity).Should().BeTrue();
            }
            File.ReadAllBytes(FileHelper.GetSuffixFile(files.Filename, "-backup", false)).Should().Equal(before);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var engine = new SharedEngine(settings);
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                for (var i = 0; i < names.Length; i++)
                    db.GetCollection(names[i]).FindAll().Should().BeEquivalentTo(expected[i]);
            }
        }
        private static void MarkInvalid(SharedOneFilePublicationFixture files)
        {
            using var factory = new FileStreamFactory(files.Filename, files.Settings.Password, false, false);
            using var stream = factory.GetStream(true, false);
            var bytes = new byte[PAGE_SIZE];
            stream.ReadFully(bytes, 0, bytes.Length);
            var page = new BufferSlice(bytes, 0, bytes.Length);
            page[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
            PageChecksum.Write(page);
            stream.Position = 0;
            stream.Write(bytes, 0, bytes.Length);
            stream.FlushToDisk();
        }
    }
}
#endif
