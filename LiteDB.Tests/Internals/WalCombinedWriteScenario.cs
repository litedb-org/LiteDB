using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    // Snapshots are taken before exception cleanup or graceful disposal can repair them.
    internal sealed class WalCombinedWriteScenario : IDisposable
    {
        internal readonly TempFile File = new TempFile();
        internal LiteEngine Engine { get; }
        internal LiteDatabase Database { get; }
        internal EngineState State { get; }
        internal ChecksummedWalStream Writer => (ChecksummedWalStream)State.Disk.WalWriterLock;
        internal byte[] Data, Before, After;
        internal int Start;
        internal string LogName => FileHelper.GetLogFile(File.Filename);

        internal WalCombinedWriteScenario(bool durable)
        {
            Engine = new LiteEngine(new EngineSettings
            {
                Filename = File.Filename, DurableCommits = durable,
                CompactStorage = CompactStorageMode.Legacy, TransactionPageLimit = 1
            });
            State = (EngineState)typeof(LiteEngine).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Engine);
            Database = new LiteDatabase(Engine, disposeOnClose: false);
            Database.CheckpointSize = 0;
            Database.GetCollection("docs").Insert(Documents(0));
            Database.GetCollection("docs").EnsureIndex("value");
            Database.GetCollection("untouched").Insert(new BsonDocument { ["_id"] = 99, ["payload"] = "sentinel" });
            Database.Checkpoint();
            Database.GetCollection("docs").Update(Documents(1));
            // The earlier acknowledged transaction is durable in both configurations.
            ((FileStream)Writer.RawStream).Flush(true);
        }

        internal void Capture()
        {
            State.AtCrashPoint = phase =>
            {
                if (phase == "wal-confirmation-before-write")
                {
                    Writer.RawStream.GetType().Should().Be(typeof(FileStream));
                    Start = checked((int)(Writer.Position / PAGE_SIZE * WalChecksum.FrameSize));
                    Start.Should().BeGreaterOrEqualTo(checked((int)(Writer.Length / PAGE_SIZE * WalChecksum.FrameSize)));
                    Before = TempFile.ReadAllBytesShared(LogName);
                    Data = TempFile.ReadAllBytesShared(File.Filename);
                }
                if (phase == "wal-confirmation-after-write")
                {
                    After = TempFile.ReadAllBytesShared(LogName);
                    // This assertion distinguishes the actual combined path before Flush/Pad.
                    After.Length.Should().Be((int)WalPadding.AlignedLength(Start + WalChecksum.FrameSize));
                    throw new IOException("captured combined write before flush");
                }
            };
            try
            {
                Action update = () => Database.GetCollection("docs").Update(Documents(2));
                update.Should().Throw<IOException>().WithMessage("captured combined write before flush");
                Before.Should().NotBeNull();
                After.Should().NotBeNull();
            }
            finally { State.AtCrashPoint = null; }
        }

        internal static BsonDocument[] Documents(int value) => Enumerable.Range(0, 16).Select(id => new BsonDocument
        {
            ["_id"] = id, ["value"] = value, ["payload"] = new string((char)('a' + id), 1500)
        }).ToArray();

        internal void AssertRecovered(byte[] wal, int value)
        {
            using var recoveredFile = new TempFile();
            var log = FileHelper.GetLogFile(recoveredFile.Filename);
            System.IO.File.WriteAllBytes(recoveredFile.Filename, Data);
            System.IO.File.WriteAllBytes(log, wal);
            try
            {
                // Repeated read-only recovery must never rewrite the evidence.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var engine = new LiteEngine(new EngineSettings { Filename = recoveredFile.Filename, ReadOnly = true });
                    using var db = new LiteDatabase(engine, disposeOnClose: false);
                    AssertDocuments(db, value);
                    System.IO.File.ReadAllBytes(recoveredFile.Filename).Should().Equal(Data);
                    System.IO.File.ReadAllBytes(log).Should().Equal(wal);
                }
                // Writable recovery, checkpoint and two subsequent opens must agree.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    using var engine = new LiteEngine(new EngineSettings { Filename = recoveredFile.Filename });
                    using var db = new LiteDatabase(engine, disposeOnClose: false);
                    AssertDocuments(db, value);
                    db.Checkpoint();
                }
            }
            finally { System.IO.File.Delete(log); }
        }

        internal static void AssertDocuments(LiteDatabase db, int value)
        {
            var docs = db.GetCollection("docs");
            docs.FindAll().Should().BeEquivalentTo(Documents(value));
            docs.Find(Query.EQ("value", value)).Should().BeEquivalentTo(Documents(value));
            docs.Find(Query.EQ("value", value == 1 ? 2 : 1)).Should().BeEmpty();
            db.GetCollection("untouched").FindAll().Should().ContainSingle()
                .Which["payload"].AsString.Should().Be("sentinel");
        }

        public void Dispose()
        {
            State.AtCrashPoint = null;
            Database.Dispose();
            Engine.Dispose();
            System.IO.File.Delete(LogName);
            File.Dispose();
        }
    }
}
