#if DEBUG || TESTING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A checkpoint writes the WAL outside the commit path: retirement records appended to the raw
    /// WAL, header journals, salt rotations. When such a write failed, the checkpoint stopped the
    /// engine only after releasing the WAL writer, index and commit locks. A commit waiting for them
    /// appended behind what the failed checkpoint left (a torn retirement record, a reserved slot it
    /// never wrote), was acknowledged, and was discarded by the next open's recovery. A failed
    /// checkpoint now publishes the stop before it releases the WAL writer. The window is forced
    /// through the coordination signal the checkpoint raises after releasing its locks.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class CheckpointFailureWindow_Tests
    {
        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void Commit_waiting_on_a_failed_retirement_record_write_is_refused(bool torn, bool ioFailure)
        {
            using var data = new MemoryStream();
            using var log = new TearingLog { Torn = torn, IoFailure = ioFailure };
            var signals = new WindowSignals { Ready = () => log.Fired };
            var settings = new EngineSettings { DataStream = data, LogStream = log, CoordinationSignals = signals };
            settings.CheckpointStage = stage => { if (stage == "retirement-before-record-write") log.Armed = true; };
            bool acknowledged;
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Enumerable.Range(1, 64).Select(id => MvccRetirementScenario.Document(id, 0)));
                for (var value = 1; value <= 5; value++) Update(db, value);
                using (var reader = engine.Query("rows", new Query()))
                {
                    reader.Read().Should().BeTrue("a live reader makes the checkpoint retire frames");
                    acknowledged = FailCheckpointWithACommitInTheWindow(db, signals, () => Update(db, 9));
                }
                log.Fired.Should().BeTrue("the checkpoint reached its retirement record write");
            }
            acknowledged.Should().BeFalse("the failed checkpoint stopped the engine before releasing the WAL writer");

            using var recovered = Recover(data, log);
            recovered.GetCollection("rows").FindAll().Select(x => x["value"].AsInt32).Should().OnlyContain(v => v == 9);
            recovered.GetCollection("rows").Count().Should().Be(64);
            recovered.GetCollection("late").Count().Should().Be(0);
        }

        /// <summary>
        /// The defence behind that stop: a salt rotation tears the data header with its journal
        /// outstanding, and a commit reaches the WAL writer before the engine stops (the timing
        /// before the stop moved inside the writer, restored by a test hook). Its append is refused
        /// and its cleanup keeps the journal, so the next open restores the header.
        /// </summary>
        [Theory]
        [InlineData(true, false)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(false, true)]
        public void Append_after_a_torn_checkpoint_header_keeps_the_journal(bool deferredStop, bool ioFailure)
        {
            using var data = new TornHeaderData { IoFailure = ioFailure };
            using var log = new MemoryStream();
            var signals = new WindowSignals { Ready = () => data.Torn };
            var settings = new EngineSettings { DataStream = data, LogStream = log, CoordinationSignals = signals };
            settings.CheckpointStage = stage => { if (stage == "before-reclaim") data.Armed = true; };
            bool acknowledged;
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                engine.SimulateDeferredCheckpointStop = deferredStop;
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Enumerable.Range(1, 20).Select(id => new BsonDocument { ["_id"] = id, ["p"] = new string('x', 300) }));
                acknowledged = FailCheckpointWithACommitInTheWindow(db, signals, () => { });
                data.Torn.Should().BeTrue("the salt rotation tore the data header");
            }
            acknowledged.Should().BeFalse("the append is refused while the header journal is outstanding");

            using var recovered = Recover(data, log);
            recovered.GetCollection("rows").Count().Should().Be(20, "the header is restored from its journal");
            recovered.GetCollection("late").Count().Should().Be(0);
        }

        /// <summary>
        /// The stop a failed checkpoint begins inside the WAL writer is completed after its locks:
        /// the engine is closed, so disposing it later runs no close-time checkpoint over the
        /// failure, and a new engine opens the file.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Failed_checkpoint_closes_the_engine(bool ioFailure)
        {
            using var file = new TempFile();
            var stages = new List<string>();
            var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            try
            {
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    // The default checkpoint size: a normal close checkpoints.
                    db.GetCollection("rows").Insert(Enumerable.Range(1, 20).Select(id => new BsonDocument { ["_id"] = id }));
                    engine.CheckpointStage = stage =>
                    {
                        if (stage == "before-reclaim") throw ioFailure ? new IOException("injected checkpoint failure") : new InvalidOperationException("injected checkpoint failure");
                    };
                    Action checkpoint = () => db.Checkpoint();
                    checkpoint.Should().Throw<Exception>().Where(x => x.Message.Contains("injected checkpoint failure"));
                }
                engine.CheckpointStage = stages.Add;
                engine.Dispose();
                stages.Should().BeEmpty("the failed checkpoint closed the engine");
            }
            finally { engine.Dispose(); }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").Count().Should().Be(20);
        }

        /// <summary>Checkpoint on a worker thread; in its window after the locks, commit on a third thread.</summary>
        private static bool FailCheckpointWithACommitInTheWindow(LiteDatabase db, WindowSignals signals, Action before)
        {
            var acknowledged = false;
            Exception checkpointFailure = null;
            var worker = new Thread(() =>
            {
                before();
                signals.OnWindow = () =>
                {
                    var committer = new Thread(() =>
                    {
                        try
                        {
                            db.GetCollection("late").Insert(new BsonDocument { ["_id"] = 1 });
                            acknowledged = true;
                        }
                        catch (Exception) { }
                    });
                    committer.Start();
                    committer.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
                };
                try { db.Checkpoint(); }
                catch (Exception ex) { checkpointFailure = ex; }
            });
            worker.Start();
            worker.Join(TimeSpan.FromSeconds(60)).Should().BeTrue();
            checkpointFailure.Should().NotBeNull();
            signals.WindowRan.Should().BeTrue("the commit ran after the checkpoint released its locks");
            return acknowledged;
        }

        /// <summary>A killed process leaves every byte the streams hold.</summary>
        private static LiteDatabase Recover(MemoryStream data, MemoryStream log) => new LiteDatabase(new LiteEngine(new EngineSettings
        {
            DataStream = new MemoryStream(data.ToArray()), LogStream = new MemoryStream(log.ToArray())
        }));

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, 64).Select(id => MvccRetirementScenario.Document(id, value)));

        private sealed class WindowSignals : ICoordinationSignals
        {
            internal Action OnWindow;
            internal Func<bool> Ready = () => true;
            internal bool WindowRan;
            public void StructuralBegin() { }
            public void StructuralEnd(int version)
            {
                if (OnWindow == null || !Ready()) return;
                var action = Interlocked.Exchange(ref OnWindow, null);
                if (action == null) return;
                WindowRan = true;
                action();
            }
            public void SlotReused() { }
            public void Committed(int version) { }
        }

        /// <summary>Armed, the next frame write stores half the frame (or nothing) and fails.</summary>
        private sealed class TearingLog : MemoryStream
        {
            internal volatile bool Armed;
            internal bool Torn, IoFailure, Fired;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed && count == WalChecksum.FrameSize)
                {
                    Armed = false;
                    Fired = true;
                    if (Torn) base.Write(buffer, offset, count / 2);
                    throw IoFailure ? new IOException("injected retirement record failure") : new UnauthorizedAccessException("injected retirement record failure");
                }
                base.Write(buffer, offset, count);
            }
        }

        /// <summary>Armed, the next header write stores its first 40 bytes and fails.</summary>
        private sealed class TornHeaderData : MemoryStream
        {
            internal volatile bool Armed;
            internal bool Torn, IoFailure;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed && Position == 0 && count == Constants.PAGE_SIZE)
                {
                    Armed = false;
                    base.Write(buffer, offset, 40);
                    Torn = true;
                    throw IoFailure ? new IOException("injected torn header write") : new UnauthorizedAccessException("injected torn header write");
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
#endif
