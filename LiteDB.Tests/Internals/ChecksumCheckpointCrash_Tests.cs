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
    public class ChecksumCheckpointCrash_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void EveryCheckpointWriteBoundary_RecoversAcknowledgedCommits(string password)
        {
            using var data = new CheckpointImages();
            using var log = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var names = Enumerable.Range(0, 10).Select(i => "collection_" + i + new string('x', 45)).ToArray();
            foreach (var name in names) db.GetCollection(name).Insert(new BsonDocument { ["_id"] = 1, ["value"] = 123 });
            var wal = log.ToArray();
            data.Capture = true;
            db.Checkpoint();
            data.Capture = false;
            data.Writes.Should().NotBeEmpty();

            // Before every write, all earlier writes reached storage but this one
            // did not. The original WAL remains until the checkpoint completes.
            foreach (var write in data.Writes)
                AssertRecovered(write.Before, wal, password, names);
            AssertRecovered(data.ToArray(), wal, password, names);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void TornCheckpointPages_RecoverExceptForTheUnprotectedHeader(string password, bool readOnly)
        {
            using var data = new CheckpointImages();
            using var log = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var names = Enumerable.Range(0, 10).Select(i => "collection_" + i + new string('x', 45)).ToArray();
            foreach (var name in names) db.GetCollection(name).Insert(new BsonDocument { ["_id"] = 1, ["value"] = 123 });
            var wal = log.ToArray();
            data.Capture = true;
            db.Checkpoint();
            data.Capture = false;
            var headerPosition = password == null ? 0 : PAGE_SIZE;
            var rejectedHeaders = 0;

            foreach (var write in data.Writes)
            {
                // Model a sector reaching storage while the rest of its page
                // retains the previous bytes, including encrypted ciphertext.
                var torn = new byte[Math.Max(write.Before.Length, write.Position + PAGE_SIZE)];
                Buffer.BlockCopy(write.Before, 0, torn, 0, write.Before.Length);
                Buffer.BlockCopy(write.Bytes, 0, torn, write.Position, 512);
                if (write.Position != headerPosition)
                {
                    AssertRecovered(torn, wal, password, names);
                    continue;
                }

                using var candidate = ChecksumTestFiles.Copy(torn);
                using var candidateWal = ChecksumTestFiles.Copy(wal);
                try
                {
                    using var recovered = new LiteEngine(new EngineSettings
                    {
                        DataStream = candidate, LogStream = candidateWal, Password = password, ReadOnly = readOnly,
                        AutoRebuild = true
                    });
                    using var recoveredDb = new LiteDatabase(recovered, disposeOnClose: false);
                    // Avoid a normal close checkpoint changing this image.
                    foreach (var name in names) recoveredDb.GetCollection(name).Count().Should().Be(1);
                }
                catch (PageChecksumException)
                {
                    rejectedHeaders++;
                    candidate.ToArray().Should().Equal(torn);
                    candidateWal.ToArray().Should().Equal(wal);
                }
                if (readOnly)
                {
                    candidate.ToArray().Should().Equal(torn);
                    candidateWal.ToArray().Should().Equal(wal);
                }
            }
            // This is a documented merge blocker, not a recovery guarantee:
            // header validation currently prevents replay of an intact redo WAL.
            rejectedHeaders.Should().BeGreaterThan(0);
        }

        [Theory]
        [InlineData(null, 8)]
        [InlineData("secret", 8)]
        [InlineData(null, 9)]
        [InlineData("secret", 9)]
        public void LegacyConversion_CanResumeAtEveryPageWriteBoundary(string password, byte version)
        {
            using var original = new WalTestDatabase(password);
            original.Seed("docs");
            original.Database.Checkpoint();
            using var data = new CheckpointImages();
            var bytes = original.Data.ToArray();
            data.Write(bytes, 0, bytes.Length);
            using var log = new MemoryStream();
            ChecksumTestFiles.MakeLegacy(data, log, password, version);
            using (var factory = new StreamFactory(data, password))
            using (var stream = factory.GetStream(true, false))
            {
                // MakeLegacy changes the header, but its source already has data
                // CRCs. Restore legacy transaction fields so conversion rewrites
                // real metadata, including changed ciphertext in encrypted files.
                var page = new byte[PAGE_SIZE];
                for (long position = PAGE_SIZE; position < stream.Length; position += PAGE_SIZE)
                {
                    stream.Position = position;
                    stream.ReadRequired(page, 0, page.Length);
                    new BufferSlice(page, 0, PAGE_SIZE).Write(uint.MaxValue, BasePage.P_TRANSACTION_ID);
                    stream.Position = position;
                    stream.Write(page, 0, page.Length);
                }
            }
            data.Capture = true;
            using (var converted = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password })) { }
            data.Capture = false;
            data.Writes.Should().HaveCount((bytes.Length - (password == null ? 0 : PAGE_SIZE)) / PAGE_SIZE);
            foreach (var write in data.Writes)
            {
                AssertRecovered(write.Before, Array.Empty<byte>(), password, new[] { "docs" }, WalTestDatabase.DocumentCount, 0);
                var torn = (byte[])write.Before.Clone();
                Buffer.BlockCopy(write.Bytes, 0, torn, write.Position, 512);
                AssertRecovered(torn, Array.Empty<byte>(), password, new[] { "docs" }, WalTestDatabase.DocumentCount, 0);
            }
        }

        [Theory]
        [InlineData(null, 64)]
        [InlineData("secret", 64)]
        [InlineData(null, 128)]
        [InlineData("secret", 128)]
        public void TornConversionHeader_IsDetectedButCannotResumeAutomatically(string password, int prefix)
        {
            using var original = new WalTestDatabase(password);
            original.Seed("docs");
            original.Database.Checkpoint();
            using var data = ChecksumTestFiles.Copy(original.Data.ToArray());
            using var log = new MemoryStream();
            ChecksumTestFiles.MakeLegacy(data, log, password);
            var legacy = data.ToArray();
            using (var converted = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password })) { }
            var bytes = data.ToArray();
            var headerPosition = password == null ? 0 : PAGE_SIZE;
            // The first part of the v10 publication reaches storage; the rest
            // retains its legacy bytes. No backup exists for in-place conversion.
            Buffer.BlockCopy(legacy, headerPosition + prefix, bytes, headerPosition + prefix, PAGE_SIZE - prefix);
            using var interrupted = ChecksumTestFiles.Copy(bytes);
            var wal = log.ToArray();
            var settings = new EngineSettings { DataStream = interrupted, LogStream = log, Password = password, AutoRebuild = true };
            Action reopen = () => { using var engine = new LiteEngine(settings); };
            reopen.Should().Throw<PageChecksumException>();
            interrupted.ToArray().Should().Equal(bytes);
            log.ToArray().Should().Equal(wal);
        }

        private static void AssertRecovered(byte[] bytes, byte[] wal, string password, string[] names, int count = 1, int value = 123)
        {
            using var data = ChecksumTestFiles.Copy(bytes);
            using var log = ChecksumTestFiles.Copy(wal);
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                foreach (var name in names)
                    db.GetCollection(name).FindAll().Should().HaveCount(count).And.OnlyContain(x => x["value"].AsInt32 == value);
                db.Checkpoint();
            }
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            foreach (var name in names) reopened.GetCollection(name).Count().Should().Be(count);
        }

        private sealed class CheckpointImages : MemoryStream
        {
            internal bool Capture;
            internal readonly List<WriteImage> Writes = new List<WriteImage>();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Capture && count == PAGE_SIZE)
                {
                    var bytes = new byte[count];
                    Buffer.BlockCopy(buffer, offset, bytes, 0, count);
                    Writes.Add(new WriteImage { Position = checked((int)Position), Before = ToArray(), Bytes = bytes });
                }
                base.Write(buffer, offset, count);
            }
        }

        private sealed class WriteImage
        {
            internal int Position;
            internal byte[] Before;
            internal byte[] Bytes;
        }
    }
}
