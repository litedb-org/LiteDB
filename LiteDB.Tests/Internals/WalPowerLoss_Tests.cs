using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class WalPowerLoss_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void PowerLossCanPersistConfirmationBeforeAMiddleFrame_WithoutPartialRecovery(string password, bool durable)
        {
            using var file = new TempFile();
            using var log = new PowerLossFile(file.Filename);
            using var data = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings
            {
                DataStream = data, LogStream = log, Password = password, DurableCommits = durable, TransactionPageLimit = 1
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var docs = db.GetCollection("docs");
            BsonDocument[] Documents(int value) => Enumerable.Range(0, 16).Select(id => new BsonDocument
            {
                ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 1500)
            }).ToArray();
            docs.Insert(Documents(0));
            db.Checkpoint();
            docs.Update(Documents(1));
            log.Flush(true); // Earlier acknowledged transaction is known durable.
            var end = log.Length;
            log.MissingPosition = end + WalChecksum.FrameSize;
            log.CrashOnSync = durable;
            if (durable)
            {
                Action update = () => docs.Update(Documents(2));
                update.Should().Throw<IOException>().WithMessage("simulated power loss");
            }
            else
            {
                docs.Update(Documents(2));
                log.PowerLoss();
            }
            using var recoveryData = ChecksumTestFiles.Copy(data.ToArray());
            using var recoveryLog = ChecksumTestFiles.Copy(log.Persisted);
            using var recoveryEngine = new LiteEngine(new EngineSettings
            {
                DataStream = recoveryData, LogStream = recoveryLog, Password = password
            });
            using var recovery = new LiteDatabase(recoveryEngine, disposeOnClose: false);
            recovery.GetCollection("docs").FindAll().Should().HaveCount(16)
                .And.OnlyContain(x => x["value"].AsInt32 == 1);
            recoveryLog.Length.Should().Be(end);
        }

        private sealed class PowerLossFile : FileStream
        {
            internal byte[] Persisted = Array.Empty<byte>();
            internal bool CrashOnSync;
            internal long MissingPosition;

            internal PowerLossFile(string path) : base(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite) { }

            public override void Flush(bool flushToDisk)
            {
                if (flushToDisk && CrashOnSync)
                {
                    CrashOnSync = false;
                    PowerLoss();
                    throw new IOException("simulated power loss");
                }
                base.Flush(flushToDisk);
                if (flushToDisk) Persisted = Snapshot();
            }

            internal void PowerLoss()
            {
                // Persist all pending writes, including confirmation, except one
                // middle frame. A real device may persist unsynced sectors in this order.
                var pending = Snapshot();
                Array.Clear(pending, checked((int)MissingPosition), WalChecksum.FrameSize);
                Persisted = pending;
            }

            private byte[] Snapshot()
            {
                base.Flush(false);
                var position = Position;
                Position = 0;
                var bytes = new byte[checked((int)Length)];
                this.ReadRequired(bytes, 0, bytes.Length);
                Position = position;
                return bytes;
            }
        }
    }
}
