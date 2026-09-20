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
            data.Log = log;
            data.Capture = true;
            db.Checkpoint();
            data.Capture = false;
            data.Writes.Should().NotBeEmpty();

            // Before every write, all earlier writes reached storage but this one
            // did not. The original WAL remains until the checkpoint completes.
            foreach (var write in data.Writes)
                AssertRecovered(write.Before, write.Wal, password, names);
            AssertRecovered(data.ToArray(), wal, password, names);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void TornCheckpointPages_IncludingHeaders_RecoverAcknowledgedCommits(string password, bool readOnly)
        {
            using var data = new CheckpointImages();
            using var log = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var names = Enumerable.Range(0, 10).Select(i => "collection_" + i + new string('x', 45)).ToArray();
            foreach (var name in names) db.GetCollection(name).Insert(new BsonDocument { ["_id"] = 1, ["value"] = 123 });
            data.Log = log;
            data.Capture = true;
            db.Checkpoint();
            data.Capture = false;
            var headerPosition = password == null ? 0 : PAGE_SIZE;
            foreach (var write in data.Writes)
            {
                var cuts = write.Position == headerPosition ? new[] { 1, 16, 64, 128, 512, 4096 } : new[] { 512 };
                foreach (var prefix in cuts)
                {
                    var torn = new byte[Math.Max(write.Before.Length, write.Position + PAGE_SIZE)];
                    Buffer.BlockCopy(write.Before, 0, torn, 0, write.Before.Length);
                    Buffer.BlockCopy(write.Bytes, 0, torn, write.Position, prefix);
                    AssertRecovered(torn, write.Wal, password, names, readOnly: readOnly);
                }
            }
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
            data.Log = log;
            data.Capture = true;
            using (var converted = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password })) { }
            data.Capture = false;
            data.Writes.Should().HaveCount((bytes.Length - (password == null ? 0 : PAGE_SIZE)) / PAGE_SIZE);
            foreach (var write in data.Writes)
            {
                AssertRecovered(write.Before, write.Wal, password, new[] { "docs" }, WalTestDatabase.DocumentCount, 0);
                foreach (var prefix in new[] { 8, 15, 512 })
                {
                    var torn = (byte[])write.Before.Clone();
                    Buffer.BlockCopy(write.Bytes, 0, torn, write.Position, prefix);
                    AssertRecovered(torn, write.Wal, password, new[] { "docs" }, WalTestDatabase.DocumentCount, 0);
                }
            }
        }

        [Theory]
        [InlineData(null, 64)]
        [InlineData("secret", 64)]
        [InlineData(null, 128)]
        [InlineData("secret", 128)]
        [InlineData(null, PAGE_SIZE)]
        [InlineData("secret", PAGE_SIZE)]
        public void TornConversionHeader_ResumesAutomatically(string password, int prefix)
        {
            using var original = new WalTestDatabase(password);
            original.Seed("docs");
            original.Database.Checkpoint();
            using var data = new CheckpointImages();
            var initial = original.Data.ToArray();
            data.Write(initial, 0, initial.Length);
            using var log = new MemoryStream();
            ChecksumTestFiles.MakeLegacy(data, log, password);
            data.Log = log;
            data.Capture = true;
            using (var converted = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password })) { }
            data.Capture = false;
            var publication = data.Writes.Last();
            var torn = (byte[])publication.Before.Clone();
            Buffer.BlockCopy(publication.Bytes, 0, torn, publication.Position, prefix);
            AssertRecovered(torn, publication.Wal, password, new[] { "docs" }, WalTestDatabase.DocumentCount, 0, readOnly: true);
            AssertRecovered(torn, publication.Wal, password, new[] { "docs" }, WalTestDatabase.DocumentCount, 0);
        }

        private static void AssertRecovered(byte[] bytes, byte[] wal, string password, string[] names, int count = 1, int value = 123, bool readOnly = false)
        {
            using var data = ChecksumTestFiles.Copy(bytes);
            using var log = ChecksumTestFiles.Copy(wal);
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password, ReadOnly = readOnly };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                foreach (var name in names)
                    db.GetCollection(name).FindAll().Should().HaveCount(count).And.OnlyContain(x => x["value"].AsInt32 == value);
                if (readOnly)
                {
                    data.ToArray().Should().Equal(bytes);
                    log.ToArray().Should().Equal(wal);
                    return;
                }
                db.Checkpoint();
            }
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            foreach (var name in names) reopened.GetCollection(name).Count().Should().Be(count);
        }

        private sealed class CheckpointImages : MemoryStream
        {
            internal bool Capture;
            internal MemoryStream Log;
            internal readonly List<WriteImage> Writes = new List<WriteImage>();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Capture && count == PAGE_SIZE)
                {
                    var bytes = new byte[count];
                    Buffer.BlockCopy(buffer, offset, bytes, 0, count);
                    Writes.Add(new WriteImage { Position = checked((int)Position), Before = ToArray(), Bytes = bytes, Wal = Log.ToArray() });
                }
                base.Write(buffer, offset, count);
            }
        }

        private sealed class WriteImage
        {
            internal int Position;
            internal byte[] Before;
            internal byte[] Bytes;
            internal byte[] Wal;
        }
    }
}
