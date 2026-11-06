using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class PhysicalWalLength_Tests
    {
        [Fact]
        public void PhysicalEof_TracksPaddingJournalTruncationAndFailedWriteRetry()
        {
            using var file = new TempFile();
            using var raw = new FileStream(file.Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            var checksum = new WalChecksum();
            checksum.Reset(new byte[16]);
            using var wal = new ChecksummedWalStream(raw, checksum, ownedWriter: true);
            var physical = wal.PhysicalStream;
            physical.Should().BeOfType<PhysicalWalStream>();
            var page = new byte[PAGE_SIZE];
            new BufferSlice(page, 0, PAGE_SIZE).Write(1u, BasePage.P_TRANSACTION_ID);
            wal.WriteFinalPadded(page, 0);
            var padded = WalPadding.AlignedLength(WalChecksum.FrameSize);
            physical.Length.Should().Be(padded);
            wal.Length.Should().Be(PAGE_SIZE);
            wal.Flush();
            raw.Length.Should().Be(padded);
            physical.Position = padded;
            physical.Write(new byte[HeaderJournal.Size], 0, HeaderJournal.Size);
            checksum.JournalBytes = HeaderJournal.Size;
            physical.Length.Should().Be(padded + HeaderJournal.Size);
            wal.Length.Should().Be(PAGE_SIZE);
            physical.SetLength(padded);
            checksum.JournalBytes = 0;
            wal.SetLength(0);
            physical.Length.Should().Be(0);
            raw.Length.Should().Be(0);
            physical.Position = 0;
            Action fail = () => physical.Write(new byte[1], 0, 2);
            fail.Should().Throw<ArgumentException>();
            physical.Length.Should().Be(0);
            physical.Write(new byte[] { 11, 22, 33 }, 0, 3);
            physical.Length.Should().Be(3);
            physical.FlushToDisk();
            raw.Length.Should().Be(3);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CallerFilesAndReaders_KeepObservingExternalLengthChanges(bool reader)
        {
            using var file = new TempFile();
            using var raw = new FileStream(file.Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            var checksum = new WalChecksum();
            using var input = reader
                ? new FileStream(file.Filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;
            using var wal = new ChecksummedWalStream(input ?? raw, checksum);
            wal.PhysicalStream.Should().BeSameAs(input ?? raw);
            wal.Length.Should().Be(0);
            using var other = new FileStream(file.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            other.SetLength(PAGE_SIZE);
            other.Flush(true);
            wal.Length.Should().Be(PAGE_SIZE);
            other.SetLength(0);
            wal.Length.Should().Be(0);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TornAppendCleanup_AllowsRetryAndRepeatedFileRecovery(bool durable)
        {
            using var source = new WalCombinedWriteScenario(durable, transactionPageLimit: 1000);
            var hits = 0;
            source.State.SimulateDiskWriteFail = page =>
            {
                if (++hits != 1) return;
                var physical = source.Writer.PhysicalStream;
                physical.Position = source.Writer.Position / PAGE_SIZE * WalChecksum.FrameSize;
                physical.Write(new byte[127], 0, 127);
                throw new IOException("partial append");
            };
            Action update = () => source.Database.GetCollection("docs").Update(WalCombinedWriteScenario.Documents(2));
            update.Should().Throw<IOException>().WithMessage("partial append");
            source.State.Stopped.Should().BeTrue();
            source.State.SimulateDiskWriteFail = null;
            update.Should().Throw<IOException>();
            source.Data = TempFile.ReadAllBytesShared(source.File.Filename);
            var bytes = TempFile.ReadAllBytesShared(source.LogName);
            source.AssertRecovered(bytes, 1);
            using var retryFile = new TempFile();
            var retryLog = FileHelper.GetLogFile(retryFile.Filename);
            File.WriteAllBytes(retryFile.Filename, source.Data);
            File.WriteAllBytes(retryLog, bytes);
            try
            {
                using (var db = new LiteDatabase(retryFile.Filename))
                {
                    db.CheckpointSize = 0;
                    db.GetCollection("docs").Update(WalCombinedWriteScenario.Documents(2));
                    WalCombinedWriteScenario.AssertDocuments(db, 2);
                    source.Data = TempFile.ReadAllBytesShared(retryFile.Filename);
                    bytes = TempFile.ReadAllBytesShared(retryLog);
                    source.AssertRecovered(bytes, 2);
                    db.Checkpoint();
                }
                using var reopened = new LiteDatabase(retryFile.Filename);
                WalCombinedWriteScenario.AssertDocuments(reopened, 2);
            }
            finally { File.Delete(retryLog); }
        }
    }
}
