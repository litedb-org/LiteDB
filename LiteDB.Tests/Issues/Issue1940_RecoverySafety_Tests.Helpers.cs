using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using FluentAssertions;
using LiteDB.Engine;

namespace LiteDB.Tests.Issues
{
    public partial class Issue1940_RecoverySafety_Tests
    {
        private const int PageSize = Constants.PAGE_SIZE;
        private const int PageIdOffset = 0;
        private const int PageTypeOffset = 4;
        private const int NextPageIdOffset = 9;
        private const int TransactionIdOffset = 14;
        private const int IsConfirmedOffset = 18;
        private const int FreeEmptyPageListOffset = 60;
        private const int CheckpointOffset = 97;
        private const int LimitSizeOffset = 101;

        private void AppendConfirmedCopyOfLastWalHeader(string logPath, uint transactionId)
        {
            var log = File.ReadAllBytes(logPath);
            var header = new byte[PageSize];

            Buffer.BlockCopy(log, log.Length - PageSize, header, 0, PageSize);
            ((PageType)header[PageTypeOffset]).Should().Be(PageType.Header);
            WriteUInt32(header, TransactionIdOffset, transactionId);
            header[IsConfirmedOffset] = 1;

            using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.Write(header, 0, header.Length);
        }

        private PageType ReadPageType(byte[] data, uint pageId)
        {
            return (PageType)data[checked((int)(pageId * PageSize)) + PageTypeOffset];
        }

        private void CheckpointFixtureWithoutHealing(string databasePath, string logPath)
        {
            var log = File.ReadAllBytes(logPath);
            var confirmedTransactions = this.FindConfirmedTransactions(log);

            using (var data = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                for (var offset = 0; offset + PageSize <= log.Length; offset += PageSize)
                {
                    var transactionId = ReadUInt32(log, offset + TransactionIdOffset);
                    if (confirmedTransactions.Contains(transactionId) == false)
                    {
                        continue;
                    }

                    var page = new byte[PageSize];
                    Buffer.BlockCopy(log, offset, page, 0, PageSize);
                    WriteUInt32(page, TransactionIdOffset, uint.MaxValue);
                    page[IsConfirmedOffset] = 0;

                    var pageId = ReadUInt32(page, PageIdOffset);
                    data.Position = pageId * (long)PageSize;
                    data.Write(page, 0, page.Length);
                }
            }

            File.Delete(logPath);
        }

        private HashSet<uint> FindConfirmedTransactions(byte[] log)
        {
            var result = new HashSet<uint>();

            for (var offset = 0; offset + PageSize <= log.Length; offset += PageSize)
            {
                if (log[offset + IsConfirmedOffset] != 0)
                {
                    result.Add(ReadUInt32(log, offset + TransactionIdOffset));
                }
            }

            return result;
        }

        private int FindLatestConfirmedPage(byte[] log, uint pageId)
        {
            var confirmedTransactions = this.FindConfirmedTransactions(log);
            var result = -1;

            for (var offset = 0; offset + PageSize <= log.Length; offset += PageSize)
            {
                if (ReadUInt32(log, offset + PageIdOffset) == pageId &&
                    confirmedTransactions.Contains(ReadUInt32(log, offset + TransactionIdOffset)))
                {
                    result = offset;
                }
            }

            result.Should().BeGreaterOrEqualTo(0, $"the fixture must contain confirmed page {pageId}");
            return result;
        }

        private void SetCheckpointToZeroInConfirmedHeaders(byte[] log)
        {
            var confirmedTransactions = this.FindConfirmedTransactions(log);

            for (var offset = 0; offset + PageSize <= log.Length; offset += PageSize)
            {
                if (log[offset + PageTypeOffset] == (byte)PageType.Header &&
                    confirmedTransactions.Contains(ReadUInt32(log, offset + TransactionIdOffset)))
                {
                    WriteUInt32(log, offset + CheckpointOffset, 0);
                }
            }
        }

        private static uint LatestConfirmedHeaderFreeList(byte[] log)
        {
            var result = uint.MaxValue;

            for (var offset = 0; offset + PageSize <= log.Length; offset += PageSize)
            {
                if (log[offset + PageTypeOffset] == (byte)PageType.Header &&
                    log[offset + IsConfirmedOffset] != 0)
                {
                    result = ReadUInt32(log, offset + FreeEmptyPageListOffset);
                }
            }

            return result;
        }

        private string ExtractFixture()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), $"litedb-issue1940-safety-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);

            ZipFile.ExtractToDirectory(
                Path.Combine(AppContext.BaseDirectory, "Resources", "Issue1940_CorruptFreeEmptyList.zip"),
                tempDirectory);

            File.Exists(this.DatabasePath(tempDirectory)).Should().BeTrue();
            File.Exists(this.LogPath(tempDirectory)).Should().BeTrue();

            return tempDirectory;
        }

        private string DatabasePath(string tempDirectory)
        {
            return Path.Combine(tempDirectory, "Issue1940_CorruptFreeEmptyList.db");
        }

        private string LogPath(string tempDirectory)
        {
            return Path.Combine(tempDirectory, "Issue1940_CorruptFreeEmptyList-log.db");
        }

        private void DeleteTempDirectory(string tempDirectory)
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] |
                bytes[offset + 1] << 8 |
                bytes[offset + 2] << 16 |
                bytes[offset + 3] << 24);
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt64(byte[] bytes, int offset, long value)
        {
            var encoded = unchecked((ulong)value);

            for (var index = 0; index < sizeof(long); index++)
            {
                bytes[offset + index] = (byte)(encoded >> (index * 8));
            }
        }

        private static MemoryStream ExpandableStream(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes, 0, bytes.Length);
            stream.Position = 0;
            return stream;
        }

        private sealed class PartialWriteOnceStream : MemoryStream
        {
            private bool _failed;

            public PartialWriteOnceStream(byte[] bytes)
            {
                base.Write(bytes, 0, bytes.Length);
                this.Position = 0;
            }

            public int WriteCalls { get; private set; }

            public override void Write(byte[] buffer, int offset, int count)
            {
                this.WriteCalls++;

                if (this._failed == false && count == PageSize)
                {
                    this._failed = true;
                    base.Write(buffer, offset, 32);
                    throw new IOException("injected torn WAL page");
                }

                base.Write(buffer, offset, count);
            }
        }

        private sealed class ReadFaultOnceStream : MemoryStream
        {
            private readonly long _targetPosition;
            private readonly int _targetVisit;
            private int _targetVisits;
            private bool _failed;

            public ReadFaultOnceStream(byte[] bytes, long targetPosition, int targetVisit)
            {
                base.Write(bytes, 0, bytes.Length);
                this.Position = 0;
                this._targetPosition = targetPosition;
                this._targetVisit = targetVisit;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                this.ReadCalls++;

                if (count == PageSize && this.Position == this._targetPosition)
                {
                    this._targetVisits++;
                }

                if (this._failed == false && this._targetVisits == this._targetVisit)
                {
                    this._failed = true;
                    throw new IOException("injected transient read failure");
                }

                return base.Read(buffer, offset, count);
            }

            public int ReadCalls { get; private set; }
            public bool FaultInjected => this._failed;
        }

        private sealed class CountingReadStream : MemoryStream
        {
            public CountingReadStream(byte[] bytes)
            {
                base.Write(bytes, 0, bytes.Length);
                this.Position = 0;
            }

            public int FullPageReads { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var result = base.Read(buffer, offset, count);

                if (count == PageSize && result == PageSize)
                {
                    this.FullPageReads++;
                }

                return result;
            }
        }
    }
}
