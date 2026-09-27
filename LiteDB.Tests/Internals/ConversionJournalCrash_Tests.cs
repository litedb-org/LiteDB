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
    public class ConversionJournalCrash_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void EveryConversionRedoWrite_CanTearWithoutPublishingPartialData(string password)
        {
            using var source = new WalTestDatabase(password);
            source.Seed("docs");
            source.Database.Checkpoint();
            using var data = ChecksumTestFiles.Copy(source.Data.ToArray());
            using var log = new CapturedLog { Data = data, Preamble = password == null ? 0 : PAGE_SIZE };
            ChecksumTestFiles.MakeLegacy(data, log, password);
            log.Capture = true;
            using (var converted = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password })) { }
            log.Capture = false;
            log.Writes.Should().NotBeEmpty();

            foreach (var write in log.Writes)
            {
                Verify(write.Data, write.Before, password);
                foreach (var prefix in new[] { 1, 8, 15, 32, 64, 128, 512, 4096, write.Bytes.Length }.Where(x => x <= write.Bytes.Length).Distinct())
                {
                    // Test a partial physical tail and an extended page whose
                    // unwritten suffix survived only as zero-filled allocation.
                    foreach (var extended in new[] { false, true })
                    {
                        var torn = new byte[write.Position + (extended ? write.Bytes.Length : prefix)];
                        Buffer.BlockCopy(write.Before, 0, torn, 0, write.Before.Length);
                        Buffer.BlockCopy(write.Bytes, 0, torn, write.Position, prefix);
                        Verify(write.Data, torn, password);
                    }
                }
            }
        }

        private static void Verify(byte[] originalData, byte[] originalLog, string password)
        {
            using var data = ChecksumTestFiles.Copy(originalData);
            using var log = ChecksumTestFiles.Copy(originalLog);
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password, ReadOnly = true };
            try
            {
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("docs").FindAll().Should().HaveCount(WalTestDatabase.DocumentCount)
                    .And.OnlyContain(x => x["value"].AsInt32 == 0);
            }
            }
            catch (LiteException error) when (error.Message.Contains("index ordering/collation requires migration")) { }
            data.ToArray().Should().Equal(originalData);
            log.ToArray().Should().Equal(originalLog);
            settings.ReadOnly = false;
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("docs").FindAll().Should().HaveCount(WalTestDatabase.DocumentCount)
                    .And.OnlyContain(x => x["value"].AsInt32 == 0);
                db.Checkpoint();
            }
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("docs").Count().Should().Be(WalTestDatabase.DocumentCount);
        }

        private sealed class CapturedLog : MemoryStream
        {
            internal bool Capture;
            internal MemoryStream Data;
            internal int Preamble;
            internal readonly List<Image> Writes = new List<Image>();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Capture && Position >= Preamble && count > 0)
                {
                    var bytes = new byte[count];
                    Buffer.BlockCopy(buffer, offset, bytes, 0, count);
                    Writes.Add(new Image { Data = Data.ToArray(), Before = ToArray(), Bytes = bytes, Position = checked((int)Position) });
                }
                base.Write(buffer, offset, count);
            }
        }

        private sealed class Image
        {
            internal byte[] Data;
            internal byte[] Before;
            internal byte[] Bytes;
            internal int Position;
        }
    }
}
