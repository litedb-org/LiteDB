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
    public class EncryptedWalCreation_Tests
    {
        internal const string Password = "preamble-recovery";

        [Fact]
        public void EveryPhysicalPreambleWriteCut_AndInterruptedCompletion_PreserveLegacyData()
        {
            var legacy = LegacyData();
            using var log = new PreambleDevice();
            using (var data = ChecksumTestFiles.Copy(legacy))
            using (var db = Open(data, log)) AssertRows(db);
            log.Images.Should().Contain(x => x.Length > 0 && x.Length < 17);
            log.Images.Should().Contain(x => x.Length > 32 && x.Length < 64);
            log.Images.Should().Contain(x => x.Length == PAGE_SIZE);
            foreach (var image in log.Images)
            {
                AssertRecovery(legacy, image);
                // Crash completion again at each physical write boundary. Sample
                // its first/middle/last byte to keep the nested matrix bounded.
                using var secondLog = new PreambleDevice(image, exhaustive: false);
                using (var data = ChecksumTestFiles.Copy(legacy))
                using (var db = Open(data, secondLog)) AssertRows(db);
                foreach (var second in secondLog.Images) AssertRecovery(legacy, second);
            }
        }

        [Theory]
        [InlineData(1, true)]
        [InlineData(7, true)]
        [InlineData(16, true)]
        [InlineData(33, true)]
        [InlineData(47, true)]
        [InlineData(63, true)]
        [InlineData(1, false)]
        [InlineData(7, false)]
        [InlineData(16, false)]
        [InlineData(33, false)]
        [InlineData(47, false)]
        [InlineData(63, false)]
        public void FileBackedShortPreamble_PreservesBytesUntilWritableRecovery(int length, bool legacy)
        {
            using var file = new TempFile();
            var wal = FileHelper.GetLogFile(file.Filename);
            var data = LegacyData(legacy);
            var prefix = Preamble().Take(length).ToArray();
            File.WriteAllBytes(file.Filename, data);
            File.WriteAllBytes(wal, prefix);
            try
            {
                var connection = new ConnectionString { Filename = file.Filename, Password = Password, ReadOnly = true };
                AssertReadOnly(() => new LiteDatabase(connection), legacy);
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                File.ReadAllBytes(wal).Should().Equal(prefix);
                connection.ReadOnly = false;
                using (var db = new LiteDatabase(connection)) AssertRows(db);
                connection.ReadOnly = true;
                using (var db = new LiteDatabase(connection)) AssertRows(db);
            }
            finally { File.Delete(wal); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WrongPasswordAndForeignPreambles_FailWithoutChangingEitherSource(bool readOnly)
        {
            var legacy = LegacyData();
            var valid = Preamble();
            var unknownMarker = valid.Take(16).ToArray();
            unknownMarker[0] = 2;
            var foreignCheck = valid.Take(48).ToArray();
            foreignCheck[37] ^= 0x80;
            var foreignTail = valid.Take(100).ToArray();
            foreignTail[90] = 0x5A;
            var foreignGap = valid.Take(31).ToArray();
            foreignGap[20] = 0x5A;
            var payloadWithoutCheck = new byte[PAGE_SIZE * 2 + 16];
            Buffer.BlockCopy(valid, 0, payloadWithoutCheck, 0, 17);
            payloadWithoutCheck[PAGE_SIZE] = 0x5A;
            foreach (var bytes in new[] { unknownMarker, foreignCheck, foreignTail, foreignGap, payloadWithoutCheck })
                AssertRejected(legacy, bytes, Password, readOnly);
            AssertRejected(legacy, valid.Take(1).ToArray(), "wrong-password", readOnly);
            AssertRejected(legacy, valid.Take(48).ToArray(), "wrong-password", readOnly);
        }

        [Fact]
        public void CompletionRetainsTheSaltAndCiphertextPrefix_AndRebuildReadsWithoutMutation()
        {
            var legacy = LegacyData();
            var prefix = Preamble().Take(47).ToArray();
            using var data = ChecksumTestFiles.Copy(legacy);
            using var log = ChecksumTestFiles.Copy(prefix);
            var errors = new List<FileReaderError>();
            using (var reader = new FileReaderV8(new EngineSettings
                { DataStream = data, LogStream = log, Password = Password }, errors))
            {
                reader.Open();
                reader.GetDocuments("rows").Should().BeEquivalentTo(Documents());
            }
            errors.Should().BeEmpty();
            data.ToArray().Should().Equal(legacy);
            log.ToArray().Should().Equal(prefix);
            using (var factory = new EngineSettings { LogStream = log, Password = Password }.CreateLogFactory())
            using (var stream = factory.GetStream(true, false)) stream.Length.Should().Be(0);
            log.ToArray().Take(prefix.Length).Should().Equal(prefix);
        }

        [Fact]
        public void ExistingEncryptedWalPayload_IsNeverReinitialized()
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            byte[] dataBytes, logBytes;
            using (var db = Open(data, log))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                rows.Insert(Documents());
                rows.EnsureIndex("value");
                dataBytes = data.ToArray();
                logBytes = log.ToArray();
            }
            logBytes.Length.Should().BeGreaterThan(PAGE_SIZE);
            AssertRecovery(dataBytes, logBytes);
        }

        private static void AssertRejected(byte[] dataBytes, byte[] logBytes, string password, bool readOnly)
        {
            using var data = ChecksumTestFiles.Copy(dataBytes);
            using var log = ChecksumTestFiles.Copy(logBytes);
            Action open = () => { using var engine = new LiteEngine(new EngineSettings
                { DataStream = data, LogStream = log, Password = password, ReadOnly = readOnly }); };
            open.Should().Throw<LiteException>();
            data.ToArray().Should().Equal(dataBytes);
            log.ToArray().Should().Equal(logBytes);
        }

        internal static void AssertRecovery(byte[] dataBytes, byte[] logBytes)
        {
            using var data = ChecksumTestFiles.Copy(dataBytes);
            using var log = ChecksumTestFiles.Copy(logBytes);
            bool migrationRequired;
            using (var copy = ChecksumTestFiles.Copy(dataBytes))
            using (var factory = new StreamFactory(copy, Password))
            using (var plain = factory.GetStream(false, false))
            {
                var header = new byte[PAGE_SIZE];
                plain.ReadRequired(header, 0, header.Length);
                migrationRequired = header[HeaderPage.P_FILE_VERSION] < HeaderPage.INDEX_FILE_VERSION ||
                    header[EnginePragmas.P_INDEX_ORDER_VERSION] != EnginePragmas.INDEX_ORDER_VERSION;
            }
            foreach (var readOnly in new[] { true, false, true })
            {
                var beforeData = data.ToArray();
                var beforeLog = log.ToArray();
                if (readOnly) AssertReadOnly(() => Open(data, log, true), migrationRequired);
                else
                {
                    using (var db = Open(data, log)) AssertRows(db);
                    migrationRequired = false;
                }
                if (readOnly)
                {
                    data.ToArray().Should().Equal(beforeData);
                    log.ToArray().Should().Equal(beforeLog);
                }
            }
        }

        private static void AssertReadOnly(Func<LiteDatabase> open, bool migrationRequired)
        {
            if (migrationRequired)
            {
                Action attempt = () => { using var db = open(); };
                attempt.Should().Throw<LiteException>().WithMessage("*index ordering/collation requires migration*");
            }
            else using (var db = open()) AssertRows(db);
        }

        internal static byte[] LegacyData(bool legacy = true)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            using (var db = Open(data, log))
            {
                db.GetCollection("rows").Insert(Documents());
                db.GetCollection("rows").EnsureIndex("value");
                db.Checkpoint();
            }
            if (legacy) ChecksumTestFiles.MakeLegacy(data, log, Password);
            return data.ToArray();
        }

        private static byte[] Preamble()
        {
            using var stream = new MemoryStream();
            using (var encrypted = new AesStream(Password, new NonClosingStream(stream), allowRecovery: false)) { }
            return stream.ToArray();
        }

        private static LiteDatabase Open(Stream data, Stream log, bool readOnly = false) => new LiteDatabase(new LiteEngine(
            new EngineSettings { DataStream = data, LogStream = log, Password = Password, ReadOnly = readOnly }));

        private static BsonDocument[] Documents() => Enumerable.Range(1, 4).Select(id => new BsonDocument
            { ["_id"] = id, ["value"] = id % 2, ["payload"] = new string((char)('a' + id), 1500) }).ToArray();

        private static void AssertRows(LiteDatabase db)
        {
            db.GetCollection("rows").FindAll().Should().BeEquivalentTo(Documents());
            for (var value = 0; value < 2; value++)
                db.GetCollection("rows").Find(Query.EQ("value", value)).Should()
                    .BeEquivalentTo(Documents().Where(x => x["value"].AsInt32 == value));
        }

        private sealed class PreambleDevice : MemoryStream, IDurableStream
        {
            private byte[] _durable;
            private readonly bool _exhaustive;
            private bool _capture = true;
            internal readonly List<byte[]> Images = new List<byte[]>();

            internal PreambleDevice(byte[] initial = null, bool exhaustive = true)
            {
                _durable = initial ?? Array.Empty<byte>();
                base.Write(_durable, 0, _durable.Length);
                Position = 0;
                _exhaustive = exhaustive;
                if (_durable.Length >= PAGE_SIZE) _capture = false;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_capture && count > 0)
                {
                    var cuts = _exhaustive ? Enumerable.Range(0, count + 1) : new[] { 0, 1, count / 2, count }.Distinct();
                    foreach (var cut in cuts)
                    {
                        var bytes = (byte[])_durable.Clone();
                        if (cut > 0)
                        {
                            if (bytes.Length < Position + cut) Array.Resize(ref bytes, checked((int)Position + cut));
                            Buffer.BlockCopy(buffer, offset, bytes, checked((int)Position), cut);
                        }
                        Images.Add(bytes);
                    }
                }
                base.Write(buffer, offset, count);
            }

            public override void WriteByte(byte value) => Write(new[] { value }, 0, 1);
            public void FlushToDisk()
            {
                _durable = ToArray();
                if (_durable.Length >= PAGE_SIZE) _capture = false;
            }
            public override void Flush() { }
        }
    }
}
