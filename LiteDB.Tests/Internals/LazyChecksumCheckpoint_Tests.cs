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
    public class LazyChecksumCheckpoint_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void TornMixedCheckpointRecoversConvertedAndUntouchedPages(string password)
        {
            using var source = new WalTestDatabase(password);
            source.Seed("cold");
            source.Seed("hot");
            source.Database.GetCollection("hot").EnsureIndex("value");
            source.Database.Checkpoint();
            using var data = new CaptureData();
            var bytes = source.Data.ToArray();
            data.Write(bytes, 0, bytes.Length);
            using var log = new MemoryStream();
            ChecksumTestFiles.MakeLegacy(data, log, password);
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("hot").UpdateMany("{ value: 42 }", "true");
            data.Log = log;
            data.Armed = true;
            db.Checkpoint();
            data.Armed = false;
            data.Writes.Should().HaveCountGreaterThan(1);
            foreach (var write in data.Writes)
                foreach (var prefix in new[] { 0, 8, 15, 31, 32, 512, 4096 })
                {
                    var torn = (byte[])write.Before.Clone();
                    Buffer.BlockCopy(write.Bytes, 0, torn, write.Position, prefix);
                    Verify(torn, write.Wal, password);
                }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void RollbackDoesNotConvertPages_AndReusedLegacyIdsStayChecksummed(string password)
        {
            using var source = new WalTestDatabase(password);
            source.Seed("drop");
            source.Seed("keep");
            source.Database.Checkpoint();
            using var data = ChecksumTestFiles.Copy(source.Data.ToArray());
            using var log = new MemoryStream();
            ChecksumTestFiles.MakeLegacy(data, log, password);
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password, TransactionPageLimit = 1 };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.Checkpoint(); // Finish the separately committed index migration before testing rollback.
                var before = data.ToArray();
                db.BeginTrans();
                db.GetCollection("keep").UpdateMany("{ value: 42 }", "true");
                db.Rollback();
                db.Checkpoint();
                // Salt rotation may rewrite page zero; no rolled-back data page is converted.
                var start = password == null ? PAGE_SIZE : 2 * PAGE_SIZE;
                data.ToArray().Skip(start).Should().Equal(before.Skip(start));
                var boundary = db.GetCollection("$database").FindAll().Single()["legacyLastPageID"].AsInt64;
                db.DropCollection("drop");
                db.Checkpoint();
                db.GetCollection("reused").Insert(new BsonDocument { ["_id"] = 100, ["payload"] = new string('r', 18000) });
                db.Checkpoint();
                db.GetCollection("$database").FindAll().Single()["lastPageID"].AsInt32.Should().Be((int)boundary);
            }
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("keep").FindAll().Should().HaveCount(WalTestDatabase.DocumentCount)
                    .And.OnlyContain(d => d["value"].AsInt32 == 0);
                db.GetCollection("reused").FindById(100)["payload"].AsString.Should().HaveLength(18000);
            }
            var pages = LazyChecksumMigration_Tests.ReadPages(data, password);
            pages.Skip(1).Should().Contain(p => p[BasePage.P_PAGE_FORMAT] == PageChecksum.Legacy)
                .And.Contain(p => p[BasePage.P_PAGE_FORMAT] == PageChecksum.Checksummed);
        }

        private static void Verify(byte[] bytes, byte[] wal, string password)
        {
            using var data = ChecksumTestFiles.Copy(bytes);
            using var log = ChecksumTestFiles.Copy(wal);
            foreach (var readOnly in new[] { true, false, true })
            {
                using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password, ReadOnly = readOnly });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.GetCollection("cold").FindAll().Should().HaveCount(WalTestDatabase.DocumentCount)
                    .And.OnlyContain(d => d["value"].AsInt32 == 0 && d["payload"].AsString == new string('x', 1500));
                db.GetCollection("hot").Find(Query.EQ("value", 42)).Should().HaveCount(WalTestDatabase.DocumentCount)
                    .And.OnlyContain(d => d["payload"].AsString == new string('x', 1500));
                if (readOnly)
                {
                    data.ToArray().Should().Equal(bytes);
                    log.ToArray().Should().Equal(wal);
                }
                else
                {
                    db.Checkpoint();
                    bytes = data.ToArray();
                    wal = log.ToArray();
                }
            }
        }

        private sealed class CaptureData : MemoryStream
        {
            internal bool Armed;
            internal MemoryStream Log;
            internal readonly List<Image> Writes = new List<Image>();
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed && count > 0)
                {
                    var bytes = new byte[count];
                    Buffer.BlockCopy(buffer, offset, bytes, 0, count);
                    Writes.Add(new Image { Before = ToArray(), Bytes = bytes, Wal = Log.ToArray(), Position = (int)Position });
                }
                base.Write(buffer, offset, count);
            }
        }

        private sealed class Image
        {
            internal byte[] Before;
            internal byte[] Bytes;
            internal byte[] Wal;
            internal int Position;
        }
    }
}
