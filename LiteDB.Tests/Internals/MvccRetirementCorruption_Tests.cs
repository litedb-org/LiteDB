using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class MvccRetirementCorruption_Tests
    {
        public static IEnumerable<object[]> Mutations()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var kind in new[] { "root-crc", "record-crc", "magic", "count", "cycle", "future-slot",
                "zero-id", "duplicate", "digest", "sequence", "floor", "missing-record", "live-frame", "root-with-journal" })
                yield return new object[] { password, kind };
        }

        [Theory]
        [MemberData(nameof(Mutations))]
        public void DamagedOrCrcValidMalformedProofs_FailClosedWithoutChangingSources(string password, string kind)
        {
            MvccRetirementScenario.Run(password, true, kind == "root-with-journal" ? "retirement-header-flushed" : null, inspect: (dataBytes, logBytes) =>
            {
                var data = Plain(dataBytes, password);
                var log = Plain(logBytes, password);
                var header = new BufferSlice(data, 0, PAGE_SIZE);
                header[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.MVCC_FILE_VERSION);
                var root = header.ReadInt64(WalRetirement.RootPosition) - PAGE_SIZE;
                var offset = checked((int)(root / PAGE_SIZE * WalChecksum.FrameSize));
                var record = new BufferSlice(log, offset, PAGE_SIZE);
                var entry = WalRetirement.EntriesPosition;
                switch (kind)
                {
                    case "root-with-journal":
                    case "root-crc": header.Write(header.ReadUInt32(WalRetirement.RootPosition + 8) ^ 1u, WalRetirement.RootPosition + 8); break;
                    case "record-crc": log[offset + entry + 24] ^= 1; break;
                    case "magic": record.Write(0u, 32); break;
                    case "count": record.Write(WalRetirement.Capacity + 1, 48); break;
                    case "cycle": record.Write(root + PAGE_SIZE, 36); break;
                    case "future-slot": record.Write(root, entry); break;
                    case "zero-id": record.Write(0u, entry + 12); break;
                    case "duplicate":
                        record.ReadInt32(48).Should().BeGreaterThan(1);
                        Buffer.BlockCopy(log, offset + entry, log, offset + entry + WalRetirement.EntrySize, WalRetirement.EntrySize);
                        break;
                    case "digest": record.Write(record.ReadInt64(entry + 24) ^ 1L, entry + 24); break;
                    case "sequence": record.Write(-1L, entry + 44); break;
                    case "floor": header.Write(header.ReadInt64(WalRetirement.RootPosition + 12) + 1, WalRetirement.RootPosition + 12); break;
                    case "missing-record": Array.Resize(ref log, offset); break;
                    case "live-frame":
                        var live = Enumerable.Range(0, log.Length / WalChecksum.FrameSize)
                            .Select(i => i * WalChecksum.FrameSize).First(i => log[i + BasePage.P_PAGE_TYPE] == (byte)PageType.Data && log[i + BasePage.P_PAGE_FORMAT] == PageChecksum.Checksummed);
                        log[live + 100] ^= 1;
                        break;
                }
                if (kind != "root-with-journal" && kind != "root-crc" && kind != "record-crc" && kind != "missing-record" && kind != "live-frame")
                {
                    // Repair all physical checksums and the root binding: this
                    // exercises semantic admission and transaction proofs.
                    var frame = new byte[WalChecksum.FrameSize];
                    Buffer.BlockCopy(log, offset, frame, 0, frame.Length);
                    var metadata = new BufferSlice(frame, PAGE_SIZE, WalChecksum.MetadataSize);
                    metadata.Write(0u, 4);
                    metadata.Write(WalRetirement.Crc(frame), 4);
                    Buffer.BlockCopy(frame, 0, log, offset, frame.Length);
                    header.Write(WalRetirement.Crc(frame), WalRetirement.RootPosition + 8);
                }
                PageChecksum.Write(header);
                var damagedData = Physical(data, password);
                var damagedLog = Physical(log, password);
                foreach (var readOnly in new[] { true, false })
                {
                    using var dataStream = ChecksumTestFiles.Copy(damagedData);
                    using var logStream = ChecksumTestFiles.Copy(damagedLog);
                    Action open = () =>
                    {
                        using var engine = new LiteEngine(new EngineSettings
                        {
                            DataStream = dataStream, LogStream = logStream, Password = password, ReadOnly = readOnly
                        });
                    };
                    open.Should().Throw<PageChecksumException>("a published retirement root cannot discard its required commits");
                    dataStream.ToArray().Should().Equal(damagedData);
                    logStream.ToArray().Should().Equal(damagedLog);
                }
            });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void TornUnconfirmedReuse_IsIgnoredOnlyAtAWitnessedSlot(string password)
        {
            MvccRetirementScenario.Run(password, true, null, inspect: (dataBytes, logBytes) =>
            {
                var data = Plain(dataBytes, password);
                var log = Plain(logBytes, password);
                var header = new BufferSlice(data, 0, PAGE_SIZE);
                var offset = checked((int)((header.ReadInt64(WalRetirement.RootPosition) / PAGE_SIZE - 1) * WalChecksum.FrameSize));
                var record = new BufferSlice(log, offset, PAGE_SIZE);
                var retired = checked((int)(record.ReadInt64(WalRetirement.EntriesPosition) / PAGE_SIZE * WalChecksum.FrameSize));
                for (var i = 0; i < WalChecksum.FrameSize; i++) log[retired + i] = (byte)(i % 251);
                MvccRetirementScenario.Verify(dataBytes, Physical(log, password), password);
            });
        }

        private static byte[] Plain(byte[] physical, string password)
        {
            using var source = ChecksumTestFiles.Copy(physical);
            using var factory = new StreamFactory(source, password);
            using var stream = factory.GetStream(false, false);
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadRequired(bytes, 0, bytes.Length);
            return bytes;
        }

        private static byte[] Physical(byte[] plain, string password)
        {
            using var result = new MemoryStream();
            using (var factory = new StreamFactory(result, password))
            using (var stream = factory.GetStream(true, false))
            {
                stream.Write(plain, 0, plain.Length);
                stream.FlushToDisk();
            }
            return result.ToArray();
        }
    }
}
