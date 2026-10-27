#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileReplacementReplay_Tests
    {
        [OneFileTheory]
        [InlineData(false, "replace")] [InlineData(true, "replace")]
        [InlineData(false, "restore-data-first")] [InlineData(true, "restore-data-first")]
        [InlineData(false, "restore-wal-first")] [InlineData(true, "restore-wal-first")]
        [InlineData(false, "identity-collision")] [InlineData(true, "identity-collision")]
        public void Retained_same_path_authority_never_overrides_replacement_DB_and_WAL(bool encrypted, string operation)
        {
            var password = encrypted ? "secret" : null;
            using var files = new SharedOneFilePublicationFixture(password);
            Guid oldAuthority;
            using (var first = new SharedEngine(files.Settings))
            {
                oldAuthority = first.MappedReaderTable.Authority.Header.Authority;
                using var db = new LiteDatabase(first, disposeOnClose: false);
                SharedOneFileWarmReads_Tests.Warm(first, db);
            }
            using var backup = new MappedTestFile();
            File.Copy(files.Filename, backup.Filename, true);
            using (var replacement = new LiteDatabase(new ConnectionString { Filename = backup.Filename, Password = password }))
            {
                replacement.CheckpointSize = 0;
                replacement.GetCollection("docs").Update(Row(1, 7));
            }
            var backupLog = FileHelper.GetLogFile(backup.Filename);
            File.Exists(backupLog).Should().BeTrue("exercise the restored DB and its acknowledged WAL as a pair");
            var oldIdentity = Identity(files.Settings);
            if (operation == "replace" || operation == "identity-collision")
            {
                File.Move(files.Filename, files.Filename + ".previous");
                File.Copy(backup.Filename, files.Filename);
                Identity(files.Settings).Should().NotBe(oldIdentity);
                File.Copy(backupLog, files.Log, true);
            }
            else
            {
                if (operation == "restore-wal-first") File.Copy(backupLog, files.Log, true);
                // FileMode.Open preserves the actual native identity, even if all bytes differ.
                using (var target = new FileStream(files.Filename, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    var bytes = File.ReadAllBytes(backup.Filename);
                    target.SetLength(bytes.Length);
                    target.Write(bytes, 0, bytes.Length);
                    target.Flush(true);
                }
                if (operation == "restore-data-first") File.Copy(backupLog, files.Log, true);
                Identity(files.Settings).Should().Be(oldIdentity);
            }
            if (operation == "identity-collision")
            {
                // Controlled native-inode-reuse input: an otherwise valid old UUID
                // and namespace with the replacement's real physical identity hash.
                var bytes = File.ReadAllBytes(files.Filename + "-shm");
                var header = bytes.Take(256).ToArray();
                Buffer.BlockCopy(SharedMemoryEnvelope.Hash(Identity(files.Settings)), 0, header, 80, 32);
                Buffer.BlockCopy(SharedMemoryEnvelope.Hash(header.Take(224).ToArray()), 0, header, 224, 32);
                Buffer.BlockCopy(header, 0, bytes, 0, 256);
                Buffer.BlockCopy(header, 0, bytes, SharedMemoryLayout.Page, 256);
                File.WriteAllBytes(files.Filename + "-shm", bytes);
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var engine = new SharedEngine(files.Settings);
                if (operation == "replace") engine.MappedReaderTable.Authority.Header.Authority.Should().NotBe(oldAuthority);
                else engine.MappedReaderTable.Authority.Header.Authority.Should().Be(oldAuthority);
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                Verify(db, 7);
                Quiet(engine.MappedReaderTable);
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = files.Filename, Password = password });
                Verify(db, 7);
            }
        }

        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public void Invalid_same_path_replacement_refuses_twice_without_changing_storage(bool encrypted)
        {
            using var files = new SharedOneFilePublicationFixture(encrypted ? "secret" : null);
            using (var engine = new SharedEngine(files.Settings)) { }
            var data = File.ReadAllBytes(files.Filename);
            // Replace with an invalid image of the same length, retaining the old authority.
            data[0] ^= 0x7f;
            File.WriteAllBytes(files.Filename, data);
            var wal = File.Exists(files.Log) ? File.ReadAllBytes(files.Log) : Array.Empty<byte>();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                Action open = () => { using var engine = new SharedEngine(files.Settings); };
                open.Should().Throw<Exception>();
                File.ReadAllBytes(files.Filename).Should().Equal(data);
                (File.Exists(files.Log) ? File.ReadAllBytes(files.Log) : Array.Empty<byte>()).Should().Equal(wal);
                files.WriteIsFree();
            }
        }

        private static string Identity(EngineSettings settings)
        {
            using var admission = NativeAdmission.Open(settings, true);
            return admission.Identity;
        }
    }
}
#endif
