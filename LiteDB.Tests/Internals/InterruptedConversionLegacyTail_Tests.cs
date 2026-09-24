using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    /// <summary>
    /// A conversion can stop before its footer is sealed while the primary
    /// header is still legacy. A released engine may then append ordinary
    /// commits and crash before its checkpoint. Those acknowledged legacy
    /// transactions must survive the next conversion.
    /// </summary>
    public class InterruptedConversionLegacyTail_Tests
    {
        // Logical WAL offsets of the header-only conversion records (see header-publication.md).
        private const long RedoPosition = PAGE_SIZE;           // after the intent page
        private const long PreparedPosition = 2 * PAGE_SIZE;   // after intent and redo
        private const long DescriptorPosition = 3 * PAGE_SIZE; // after intent, redo, prepared

        [Theory]
        [InlineData(null, RedoPosition, 1, 2u)]
        [InlineData("secret", RedoPosition, 1, 2u)]
        [InlineData(null, RedoPosition, 4, 2u)]
        [InlineData("secret", RedoPosition, 4, 40u)]
        [InlineData(null, PreparedPosition, 1, 2u)]
        [InlineData("secret", PreparedPosition, 4, 2u)]
        [InlineData(null, DescriptorPosition, 1, 2u)]
        [InlineData("secret", DescriptorPosition, 1, 2u)]
        [InlineData(null, DescriptorPosition, 4, 2u)]
        [InlineData("secret", DescriptorPosition, 4, 40u)]
        // Header-only commits whose IDs share the records' low 16 bits (1).
        [InlineData(null, RedoPosition, 1, 65537u)]
        [InlineData("secret", RedoPosition, 1, 65537u)]
        [InlineData(null, PreparedPosition, 1, 131073u)]
        [InlineData("secret", PreparedPosition, 1, 65537u)]
        [InlineData(null, DescriptorPosition, 1, 65537u)]
        [InlineData("secret", DescriptorPosition, 1, 131073u)]
        [InlineData(null, DescriptorPosition, 4, 131073u)]
        public void LegacyCommitsAfterAnUnsealedConversionBackup_SurviveConversion(string password, long stopAt, int legacyCommits, uint firstTransaction)
        {
            using var data = new MemoryStream();
            using var log = new WalWriteFailure();
            Interrupt(data, log, password, stopAt);
            var header = ReadLegacyHeader(data, password);
            using (var factory = new StreamFactory(log, password))
            using (var stream = factory.GetStream(true, false))
            {
                for (var i = 0; i < legacyCommits; i++)
                    AppendLegacyCommit(stream, header, firstTransaction + (uint)i, 100 + i);
                stream.FlushToDisk();
            }
            var expected = 100 + legacyCommits - 1;

            // Read-only recovery sees the acknowledged commits and preserves both files. The legacy
            // index scan keeps a v8 file readable without its pending index migration.
            var dataBefore = data.ToArray();
            var logBefore = log.ToArray();
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password, ReadOnly = true, LegacyIndexScan = true }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.UserVersion.Should().Be(expected, "the released engine's last commit was acknowledged");
                db.GetCollection("docs").Count().Should().Be(WalTestDatabase.DocumentCount);
            }
            data.ToArray().Should().Equal(dataBefore);
            log.ToArray().Should().Equal(logBefore);

            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.UserVersion.Should().Be(expected, "the released engine's last commit was acknowledged");
                db.GetCollection("docs").Count().Should().Be(WalTestDatabase.DocumentCount);
                db.Checkpoint();
            }
            ReadHeader(data, password)[HeaderPage.P_FILE_VERSION].Should().BeGreaterOrEqualTo(HeaderPage.CHECKSUM_FILE_VERSION, "conversion restarts after the legacy checkpoint");
            using var reopenedEngine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.UserVersion.Should().Be(expected);
            reopened.GetCollection("docs").Count().Should().Be(WalTestDatabase.DocumentCount);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void LegacyCommitsAfterATornConversionRecord_AreRefusedWithoutChangingFiles(string password)
        {
            using var data = new MemoryStream();
            using var log = new WalWriteFailure();
            Interrupt(data, log, password, RedoPosition);
            var header = ReadLegacyHeader(data, password);
            using (var factory = new StreamFactory(log, password))
            using (var stream = factory.GetStream(true, false))
            {
                // A torn redo keeps its first block: page 0, header links, transaction 1.
                var torn = (byte[])header.Array.Clone();
                new BufferSlice(torn, 0, PAGE_SIZE).Write(1u, BasePage.P_TRANSACTION_ID);
                Array.Clear(torn, 16, PAGE_SIZE - 16);
                stream.Position = stream.Length;
                stream.Write(torn, 0, PAGE_SIZE);
                AppendLegacyCommit(stream, header, 2, 100);
                stream.FlushToDisk();
            }

            var dataBefore = data.ToArray();
            var logBefore = log.ToArray();
            foreach (var readOnly in new[] { true, false })
            {
                Action open = () =>
                {
                    using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password, ReadOnly = readOnly, LegacyIndexScan = true });
                };
                open.Should().Throw<PageChecksumException>("an unverifiable tail must not be discarded or replayed");
                data.ToArray().Should().Equal(dataBefore);
                log.ToArray().Should().Equal(logBefore);
            }
        }

        [Theory]
        [InlineData(null, RedoPosition)]
        [InlineData("secret", RedoPosition)]
        [InlineData(null, PreparedPosition)]
        [InlineData("secret", PreparedPosition)]
        public void ConfirmedTransactionOneOutsideTheConfirmationSlot_IsRefusedWithoutChangingFiles(string password, long stopAt)
        {
            // Released engines continue after the records' transaction 1, so a
            // confirmed header page reusing it cannot be attributed to either writer.
            using var data = new MemoryStream();
            using var log = new WalWriteFailure();
            Interrupt(data, log, password, stopAt);
            var header = ReadLegacyHeader(data, password);
            using (var factory = new StreamFactory(log, password))
            using (var stream = factory.GetStream(true, false))
            {
                AppendLegacyCommit(stream, header, 1, 100);
                stream.FlushToDisk();
            }
            ExpectRefusal(data, log, password);
        }

        [Theory]
        [InlineData(null, RedoPosition, 16)]
        [InlineData(null, RedoPosition, 20)]
        [InlineData(null, PreparedPosition, 32)]
        [InlineData(null, DescriptorPosition, 20)]
        [InlineData(null, DescriptorPosition, 100)]
        [InlineData("secret", RedoPosition, 16)]
        [InlineData("secret", PreparedPosition, 20)]
        [InlineData("secret", DescriptorPosition, 32)]
        [InlineData("secret", DescriptorPosition, 100)]
        public void TornConversionRecord_IsDiscardedAndConversionCompletes(string password, long stopAt, int cut)
        {
            using var data = new MemoryStream();
            using var log = new WalWriteFailure();
            Interrupt(data, log, password, stopAt);
            // The descriptor slot holds the conversion's confirmed page.
            AppendTornRecord(log, password, ReadLegacyHeader(data, password), stopAt == DescriptorPosition, cut);

            var dataBefore = data.ToArray();
            var logBefore = log.ToArray();
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password, ReadOnly = true, LegacyIndexScan = true }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
                db.GetCollection("docs").Count().Should().Be(WalTestDatabase.DocumentCount);
            data.ToArray().Should().Equal(dataBefore);
            log.ToArray().Should().Equal(logBefore);

            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("docs").Count().Should().Be(WalTestDatabase.DocumentCount);
                db.Checkpoint();
            }
            ReadHeader(data, password)[HeaderPage.P_FILE_VERSION].Should().BeGreaterOrEqualTo(HeaderPage.CHECKSUM_FILE_VERSION);
        }

        private static void ExpectRefusal(MemoryStream data, WalWriteFailure log, string password)
        {
            var dataBefore = data.ToArray();
            var logBefore = log.ToArray();
            foreach (var readOnly in new[] { true, false })
            {
                Action open = () =>
                {
                    using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password, ReadOnly = readOnly, LegacyIndexScan = true });
                };
                open.Should().Throw<PageChecksumException>("an unattributable tail must not be discarded or replayed");
                data.ToArray().Should().Equal(dataBefore);
                log.ToArray().Should().Equal(logBefore);
            }
        }

        /// <summary>
        /// Models a torn write of a conversion record (transaction 1) that kept only
        /// its first <paramref name="cut"/> bytes. Encrypted tears leave raw zeros,
        /// which decrypt to garbage from the torn AES block onward.
        /// </summary>
        private static void AppendTornRecord(WalWriteFailure log, string password, BufferSlice header, bool confirmed, int cut)
        {
            header.Write(1u, BasePage.P_TRANSACTION_ID);
            header.Write(confirmed, BasePage.P_IS_CONFIRMED);
            var start = log.Length;
            if (password == null)
            {
                Array.Clear(header.Array, cut, PAGE_SIZE - cut);
                log.Position = start;
                log.Write(header.Array, 0, PAGE_SIZE);
                return;
            }
            using (var factory = new StreamFactory(log, password))
            using (var stream = factory.GetStream(true, false))
            {
                stream.Position = stream.Length;
                stream.Write(header.Array, 0, PAGE_SIZE);
                stream.FlushToDisk();
            }
            log.Position = start + cut;
            log.Write(new byte[PAGE_SIZE - cut], 0, PAGE_SIZE - cut);
        }

        private static void Interrupt(MemoryStream data, WalWriteFailure log, string password, long stopAt)
        {
            using (var source = new WalTestDatabase(password))
            {
                source.Seed("docs");
                source.Database.Checkpoint();
                var original = source.Data.ToArray();
                data.Write(original, 0, original.Length);
            }
            ChecksumTestFiles.MakeLegacy(data, log, password);
            var preamble = password == null ? 0 : PAGE_SIZE;

            // Stop the new engine's conversion before its footer is sealed.
            log.FailAt = preamble + stopAt;
            Action convert = () => { using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }); };
            convert.Should().Throw<IOException>().WithMessage("stop conversion");
            log.Length.Should().Be(preamble + stopAt, "the unsealed conversion records remain in the WAL");
            ReadHeader(data, password)[HeaderPage.P_FILE_VERSION].Should().Be(8, "the primary is unchanged before sealing");
        }

        private static BufferSlice ReadLegacyHeader(MemoryStream data, string password) =>
            new BufferSlice(ReadHeader(data, password), 0, PAGE_SIZE);

        private static byte[] ReadHeader(MemoryStream data, string password)
        {
            var header = new byte[PAGE_SIZE];
            using var factory = new StreamFactory(data, password);
            using var stream = factory.GetStream(false, false);
            stream.ReadRequired(header, 0, PAGE_SIZE);
            return header;
        }

        /// <summary>
        /// A released engine reads the unchanged legacy primary and numbers its
        /// transactions after the conversion's unconfirmed transaction 1. A
        /// header-only commit (a pragma write) appends one confirmed header page.
        /// </summary>
        private static void AppendLegacyCommit(Stream stream, BufferSlice header, uint transactionID, int userVersion)
        {
            header.Write(transactionID, BasePage.P_TRANSACTION_ID);
            header.Write(true, BasePage.P_IS_CONFIRMED);
            header.Write(userVersion, EnginePragmas.P_USER_VERSION);
            stream.Position = stream.Length;
            stream.Write(header.Array, 0, PAGE_SIZE);
        }

        private sealed class WalWriteFailure : MemoryStream
        {
            internal long FailAt = long.MaxValue;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Position >= FailAt)
                {
                    FailAt = long.MaxValue;
                    throw new IOException("stop conversion");
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
