using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class ConversionIntentRecovery_Tests
    {
        [Theory]
        [InlineData(null, 8)]
        [InlineData("secret", 8)]
        [InlineData(null, 9)]
        [InlineData("secret", 9)]
        public void InterruptedIntentRecovery_DoesNotRewriteHeaderBeforeSealedRedo(string password, byte version)
        {
            using var source = new WalTestDatabase(password);
            source.Seed("docs");
            source.Database.Checkpoint();
            using var data = new FirstWriteCapture();
            var original = source.Data.ToArray();
            data.Write(original, 0, original.Length);
            using var log = new MemoryStream();
            ChecksumTestFiles.MakeLegacy(data, log, password, version);
            using (var dataFactory = new StreamFactory(data, password))
            using (var dataStream = dataFactory.GetStream(false, false))
            using (var logFactory = new StreamFactory(log, password))
            using (var logStream = logFactory.GetStream(true, false))
            {
                var header = new byte[PAGE_SIZE];
                dataStream.ReadRequired(header, 0, header.Length);
                HeaderJournal.BackupLegacyHeader(logStream, header);
                // Crash after syncing only the intent, before sealing redo.
                logStream.SetLength(PAGE_SIZE);
                logStream.FlushToDisk();
            }
            data.Log = log;
            data.Armed = true;
            using (var converted = new LiteEngine(new EngineSettings
            { DataStream = data, LogStream = log, Password = password })) { }
            data.Bytes.Should().NotBeNull();
            using (var capturedLog = ChecksumTestFiles.Copy(data.Wal))
            using (var factory = new StreamFactory(capturedLog, password))
            using (var stream = factory.GetStream(false, false))
            {
                var journal = HeaderJournal.Read(stream);
                journal.Should().NotBeNull();
                journal.IntentOnly.Should().BeFalse("a second torn header write must have sealed redo");
            }
            foreach (var prefix in new[] { 0, 1, 8, 15, 16, 31, 32, 511, 512, 4096, 8191 })
            {
                var torn = (byte[])data.Before.Clone();
                Buffer.BlockCopy(data.Bytes, 0, torn, data.WritePosition, prefix);
                using var recoveredData = ChecksumTestFiles.Copy(torn);
                using var recoveredLog = ChecksumTestFiles.Copy(data.Wal);
                foreach (var readOnly in new[] { true, false, true })
                {
                    var beforeData = recoveredData.ToArray();
                    var beforeLog = recoveredLog.ToArray();
                    try
                    {
                    using (var engine = new LiteEngine(new EngineSettings
                    { DataStream = recoveredData, LogStream = recoveredLog, Password = password, ReadOnly = readOnly }))
                    using (var db = new LiteDatabase(engine, disposeOnClose: false))
                    {
                        db.GetCollection("docs").FindAll().Should().HaveCount(WalTestDatabase.DocumentCount)
                            .And.OnlyContain(row => row["value"].AsInt32 == 0 && row["payload"].AsString == new string('x', 1500));
                        if (!readOnly) db.Checkpoint();
                    }
                    }
                    catch (LiteException error) when (readOnly && error.Message.Contains("index ordering/collation requires migration")) { }
                    if (readOnly)
                    {
                        recoveredData.ToArray().Should().Equal(beforeData);
                        recoveredLog.ToArray().Should().Equal(beforeLog);
                    }
                }
            }
        }

        private sealed class FirstWriteCapture : MemoryStream
        {
            internal bool Armed;
            internal MemoryStream Log;
            internal byte[] Before;
            internal byte[] Bytes;
            internal byte[] Wal;
            internal int WritePosition;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed && count != 0)
                {
                    Armed = false;
                    Before = ToArray();
                    Bytes = buffer.Skip(offset).Take(count).ToArray();
                    Wal = Log.ToArray();
                    WritePosition = checked((int)Position);
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
