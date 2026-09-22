using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class HeaderJournalFailure_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void FailedConversionBackupSync_CannotPublishAPartialLegacyTransaction(string password)
        {
            using var original = new WalTestDatabase(password);
            original.Seed("docs");
            original.Database.Checkpoint();
            using var data = ChecksumTestFiles.Copy(original.Data.ToArray());
            using var log = new FaultStream { Mode = "conversion-sync" };
            ChecksumTestFiles.MakeLegacy(data, log, password);
            var bytes = data.ToArray();
            log.Armed = true;
            Action open = () => { using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }); };
            open.Should().Throw<IOException>().WithMessage("journal fault");
            data.ToArray().Should().Equal(bytes);
            // The failed sync may lose any preceding redo page. None of those
            // pages may be confirmed, so reopening must use the unchanged data.
            var wal = log.ToArray();
            Array.Clear(wal, (password == null ? 0 : PAGE_SIZE) + PAGE_SIZE, PAGE_SIZE);
            using var damagedLog = ChecksumTestFiles.Copy(wal);
            using var reopenedEngine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = damagedLog, Password = password });
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("docs").FindAll().Should().HaveCount(WalTestDatabase.DocumentCount)
                .And.OnlyContain(x => x["value"].AsInt32 == 0);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void DisposingAfterCheckpoint_DoesNotReextendTheTruncatedWal(string password)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
                db.Checkpoint();
            }
            log.Length.Should().Be(password == null ? 0 : PAGE_SIZE);
        }

        [Theory]
        [InlineData(null, "journal-write")]
        [InlineData("secret", "journal-write")]
        [InlineData(null, "journal-partial")]
        [InlineData("secret", "journal-partial")]
        [InlineData(null, "journal-sync")]
        [InlineData("secret", "journal-sync")]
        public void JournalMustBeDurableBeforeCheckpointTouchesData(string password, string failure)
        {
            using var data = new MemoryStream();
            using var log = new FaultStream { Mode = failure };
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 123 });
                var original = data.ToArray();
                log.Armed = true;
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>().WithMessage("journal fault");
                data.ToArray().Should().Equal(original);
                Action write = () => db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 });
                write.Should().Throw<IOException>();
            }
            AssertRecovery(data.ToArray(), log.ToArray(), password);
        }

        [Theory]
        [InlineData(null, "header-write")]
        [InlineData("secret", "header-write")]
        [InlineData(null, "header-sync")]
        [InlineData("secret", "header-sync")]
        public void FailedHeaderRepair_PreservesRecoveryJournalForAnotherOpen(string password, string failure)
        {
            CrashDuringHeaderPublication(password, out var dataBytes, out var walBytes);
            using var data = new FaultStream { Mode = failure, HeaderPosition = password == null ? 0 : PAGE_SIZE };
            data.Write(dataBytes, 0, dataBytes.Length);
            data.Armed = true;
            using var log = ChecksumTestFiles.Copy(walBytes);
            Action open = () => { using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }); };
            open.Should().Throw<IOException>().WithMessage("journal fault");
            log.ToArray().Should().Equal(walBytes);
            AssertRecovery(data.ToArray(), log.ToArray(), password);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void RecoveryJournalSyncFailure_MustNotOverwriteTheDamagedPrimary(string password)
        {
            CrashDuringHeaderPublication(password, out var dataBytes, out var walBytes);
            using var data = ChecksumTestFiles.Copy(dataBytes);
            using var log = new FaultStream { Mode = "recovery-sync" };
            log.Write(walBytes, 0, walBytes.Length);
            log.Armed = true;
            Action open = () => { using var engine = new LiteEngine(new EngineSettings
                { DataStream = data, LogStream = log, Password = password }); };
            open.Should().Throw<IOException>().WithMessage("journal fault");
            data.ToArray().Should().Equal(dataBytes);
            log.ToArray().Should().Equal(walBytes);
            AssertRecovery(data.ToArray(), log.ToArray(), password);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void RebuildReader_UsesJournalWithoutMutatingTheSources(string password)
        {
            CrashDuringHeaderPublication(password, out var dataBytes, out var walBytes);
            using var data = ChecksumTestFiles.Copy(dataBytes);
            using var log = ChecksumTestFiles.Copy(walBytes);
            var errors = new List<FileReaderError>();
            using var reader = new FileReaderV8(new EngineSettings { DataStream = data, LogStream = log, Password = password }, errors);
            reader.Open();
            reader.GetDocuments("docs").Should().ContainSingle().Which["value"].AsInt32.Should().Be(123);
            errors.Should().BeEmpty();
            data.ToArray().Should().Equal(dataBytes);
            log.ToArray().Should().Equal(walBytes);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void CorruptedRecoveryCopy_CannotCertifyADamagedHeader(string password)
        {
            CrashDuringHeaderPublication(password, out var dataBytes, out var walBytes);
            walBytes[walBytes.Length - HeaderJournal.Size + 400] ^= 1;
            using var data = ChecksumTestFiles.Copy(dataBytes);
            using var log = ChecksumTestFiles.Copy(walBytes);
            Action open = () => { using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }); };
            open.Should().Throw<PageChecksumException>();
            data.ToArray().Should().Equal(dataBytes);
            log.ToArray().Should().Equal(walBytes);
        }

        [Theory]
        [InlineData(null, "truncate-before")]
        [InlineData("secret", "truncate-before")]
        [InlineData(null, "truncate-after")]
        [InlineData("secret", "truncate-after")]
        public void InterruptedJournalRemoval_KeepsTheSyncedHeaderAndRedo(string password, string failure)
        {
            CrashDuringHeaderPublication(password, out var dataBytes, out var walBytes);
            using var data = ChecksumTestFiles.Copy(dataBytes);
            using var log = new FaultStream { Mode = failure };
            log.Write(walBytes, 0, walBytes.Length);
            log.Armed = true;
            Action open = () => { using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }); };
            open.Should().Throw<IOException>().WithMessage("journal fault");
            var prefix = walBytes.Length - HeaderJournal.Size;
            log.ToArray().Take(prefix).Should().Equal(walBytes.Take(prefix));
            AssertRecovery(data.ToArray(), log.ToArray(), password);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void FileBackedRecovery_PreservesReadOnlyImagesAndRepairsWritableOpens(string password)
        {
            CrashDuringHeaderPublication(password, out var dataBytes, out var walBytes);
            using var file = new TempFile();
            var logFile = FileHelper.GetLogFile(file.Filename);
            File.WriteAllBytes(file.Filename, dataBytes);
            File.WriteAllBytes(logFile, walBytes);
            try
            {
                var connection = new ConnectionString { Filename = file.Filename, Password = password, ReadOnly = true };
                using (var db = new LiteDatabase(connection)) db.GetCollection("docs").FindById(1)["value"].AsInt32.Should().Be(123);
                File.ReadAllBytes(file.Filename).Should().Equal(dataBytes);
                File.ReadAllBytes(logFile).Should().Equal(walBytes);
                connection.ReadOnly = false;
                using (var db = new LiteDatabase(connection)) db.Checkpoint();
                using (var db = new LiteDatabase(connection)) db.GetCollection("docs").FindById(1)["value"].AsInt32.Should().Be(123);
            }
            finally { File.Delete(logFile); }
        }

        private static void CrashDuringHeaderPublication(string password, out byte[] dataBytes, out byte[] walBytes)
        {
            using var data = new FaultStream { Mode = "torn-header", HeaderPosition = password == null ? 0 : PAGE_SIZE };
            using var log = new MemoryStream();
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 0 });
                db.Checkpoint();
                db.GetCollection("docs").Update(new BsonDocument { ["_id"] = 1, ["value"] = 123 });
                data.Armed = true;
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>().WithMessage("journal fault");
            }
            dataBytes = data.ToArray();
            walBytes = log.ToArray();
        }

        private static void AssertRecovery(byte[] dataBytes, byte[] walBytes, string password)
        {
            using var data = ChecksumTestFiles.Copy(dataBytes);
            using var log = ChecksumTestFiles.Copy(walBytes);
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("docs").FindAll().Should().ContainSingle().Which["value"].AsInt32.Should().Be(123);
                db.Checkpoint();
            }
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("docs").FindAll().Single()["value"].AsInt32.Should().Be(123);
        }

        private sealed class FaultStream : MemoryStream
        {
            internal string Mode;
            internal long HeaderPosition;
            internal bool Armed;
            private bool _pendingSync;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed && Mode == "conversion-sync" && count == PAGE_SIZE) _pendingSync = true;
                if (Armed && Mode.StartsWith("journal") && count == HeaderJournal.Size)
                {
                    if (Mode == "journal-write") Fail();
                    if (Mode == "journal-partial")
                    {
                        base.Write(buffer, offset, PAGE_SIZE + 128);
                        Fail();
                    }
                    _pendingSync = true;
                }
                if (Armed && (Mode.StartsWith("header") || Mode == "torn-header") && Position == HeaderPosition && count == PAGE_SIZE)
                {
                    if (Mode == "header-write") Fail();
                    if (Mode == "torn-header")
                    {
                        base.Write(buffer, offset, 64);
                        Fail();
                    }
                    _pendingSync = true;
                }
                base.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                if (Armed && (_pendingSync || Mode == "recovery-sync")) Fail();
                base.Flush();
            }

            public override void SetLength(long value)
            {
                if (Armed && Mode.StartsWith("truncate"))
                {
                    if (Mode == "truncate-after") base.SetLength(value);
                    Fail();
                }
                base.SetLength(value);
            }

            private void Fail()
            {
                Armed = false;
                throw new IOException("journal fault");
            }
        }
    }
}
