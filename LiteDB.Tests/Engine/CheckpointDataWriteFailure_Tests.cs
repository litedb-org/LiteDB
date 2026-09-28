using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// SimulateDataWriteFail fails a data-file page write of the checkpoint backfill on
    /// real files. The checkpoint must report the failure, the engine must stop rather
    /// than build on a half-backfilled data file, and the untouched WAL must recover
    /// every committed row on the next open.
    /// </summary>
    public class CheckpointDataWriteFailure_Tests
    {
        private const string Failure = "Simulated data page write failure";
        private const int LaterWrite = 5;

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Control_HookSeesEveryBackfilledDataPage(string password)
        {
            using var file = new DatabaseFiles(password);
            using var engine = file.OpenEngine();
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var positions = new List<long>();
            engine.SimulateDataWriteFail = page => positions.Add(page.Position);

            var pages = engine.Checkpoint();

            pages.Should().BeGreaterThan(LaterWrite, "the failing cases need a later page to fail");
            positions.Should().HaveCount(pages, "each backfilled data page passes the hook once");
            positions.Should().OnlyHaveUniqueItems().And.OnlyContain(position => position % PAGE_SIZE == 0);
            CommittedRows.Verify(db, false);
        }

        [Theory]
        [InlineData(null, 1)]
        [InlineData(null, LaterWrite)]
        [InlineData("secret", 1)]
        [InlineData("secret", LaterWrite)]
        public void FailedDataPageWrite_StopsTheEngine_AndTheWalRecoversEveryCommit(string password, int failingWrite)
        {
            using var file = new DatabaseFiles(password);
            var writes = 0;
            using (var engine = file.OpenEngine())
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var dataBefore = TempFile.ReadAllBytesShared(file.Data);
                var walBefore = TempFile.ReadAllBytesShared(file.Log);
                engine.SimulateDataWriteFail = page =>
                {
                    if (++writes == failingWrite) throw new IOException(Failure);
                };
                Action checkpoint = () => engine.Checkpoint();
                checkpoint.Should().Throw<IOException>().WithMessage(Failure);
                writes.Should().Be(failingWrite, "no data page may be written after the failed one");

                // The engine is stopped: no later operation may build on the uncertain data file.
                var dataAfterFailure = TempFile.ReadAllBytesShared(file.Data);
                var rows = db.GetCollection(CommittedRows.Collection);
                Action insert = () => rows.Insert(CommittedRows.Extra);
                insert.Should().Throw<IOException>().WithMessage("Engine closed after an I/O failure*" + Failure);
                Action read = () => rows.FindById(1);
                read.Should().Throw<IOException>();
                checkpoint.Should().Throw<IOException>();
                writes.Should().Be(failingWrite, "a retried checkpoint on the stopped engine writes nothing");
                TempFile.ReadAllBytesShared(file.Data).Should().Equal(dataAfterFailure);
                AssertCommittedFramesKept(walBefore, TempFile.ReadAllBytesShared(file.Log), password);
                if (failingWrite == 1) dataAfterFailure.Should().Equal(dataBefore, "the first page write never happened");
                else dataAfterFailure.Should().NotEqual(dataBefore, "earlier pages were backfilled before the failure");
            }

            // A fresh open replays the WAL over the partially backfilled data file.
            using (var db = file.Open())
            {
                CommittedRows.Verify(db, false);
                db.GetCollection(CommittedRows.Collection).Insert(CommittedRows.Extra);
                db.Checkpoint();
            }
            File.Exists(file.Log).Should().BeFalse("the later checkpoint completed and retired the WAL");
            var checkpointed = File.ReadAllBytes(file.Data);
            (checkpointed.Length % PAGE_SIZE).Should().Be(0);

            // The data file alone now holds the complete committed state.
            using (var reader = file.Open(readOnly: true)) CommittedRows.Verify(reader, true);
            File.ReadAllBytes(file.Data).Should().Equal(checkpointed);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void FailedCloseCheckpoint_LeavesTheWalToRecoverEveryCommit(string password)
        {
            using var file = new DatabaseFiles(password);
            var writes = 0;
            using (var engine = file.OpenEngine())
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                // A positive pragma makes the close backfill the WAL; no commit reaches it first.
                db.CheckpointSize = 10000;
                engine.SimulateDataWriteFail = page =>
                {
                    writes++;
                    throw new IOException(Failure);
                };
            }
            // Dispose returned normally although the close failed: #3047 tracks surfacing it.
            writes.Should().Be(1, "the close checkpoint reached the data write and stopped at its failure");
            new FileInfo(file.Log).Length.Should().BeGreaterThan(0, "the WAL must survive a failed close checkpoint");

            using (var db = file.Open())
            {
                CommittedRows.Verify(db, false);
                db.Checkpoint();
            }
            using (var reader = file.Open(readOnly: true)) CommittedRows.Verify(reader, false);
        }

        /// <summary>
        /// Before overwriting data the checkpoint appends only its header recovery copy
        /// (the header journal); the committed frames, the recovery source, stay as they were.
        /// </summary>
        private static void AssertCommittedFramesKept(byte[] before, byte[] after, string password)
        {
            var preamble = password == null ? 0 : PAGE_SIZE;
            var frames = preamble + (before.Length - preamble) / WalChecksum.FrameSize * WalChecksum.FrameSize;
            after.Length.Should().Be(before.Length + HeaderJournal.Size, "only the header journal may be added");
            after.Take(frames).Should().Equal(before.Take(frames), "committed WAL frames are the recovery source");
        }

        private sealed class DatabaseFiles : IDisposable
        {
            private readonly TempFile _file = new TempFile();
            private readonly string _password;

            internal DatabaseFiles(string password)
            {
                _password = password;
                using (var db = this.Open()) CommittedRows.Write(db);
                new FileInfo(this.Log).Length.Should().BeGreaterThan(0, "the latest commits must be only in the WAL");
            }

            internal string Data => _file.Filename;

            internal string Log => FileHelper.GetLogFile(_file.Filename);

            internal LiteEngine OpenEngine() => new LiteEngine(new EngineSettings { Filename = this.Data, Password = _password });

            internal LiteDatabase Open(bool readOnly = false) => new LiteDatabase(new ConnectionString
            {
                Filename = this.Data, Password = _password, ReadOnly = readOnly
            });

            public void Dispose()
            {
                if (File.Exists(this.Log)) File.Delete(this.Log);
                _file.Dispose();
            }
        }
    }
}
