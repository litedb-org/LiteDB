#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Sticky failures and the read-only reopen (decision 6 of docs/decisions/durability-policy.md):
    /// the engine a failure stopped is the one that records it, calls wait for a reopen in progress,
    /// Dispose closes what a reopen opened, and every later write, an explicit transaction or
    /// checkpoint included, throws the recorded failure.
    /// </summary>
    public partial class StickyFailure_Tests
    {
        /// <summary>
        /// A commit succeeded and its automatic checkpoint failed after it wrote a page (recorded).
        /// Another call reopened the engine before the committing call checked for the record, on the
        /// new engine, which has none: the successful commit threw the checkpoint's failure. It now
        /// checks the engine it committed to, and returns (decisions 5 and 6).
        /// </summary>
        [Fact]
        public void Commit_returns_when_another_call_reopens_the_engine_after_its_automatic_checkpoint_failed()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                using var power = new FilePowerLossModel(file.Filename);
                LiteDatabase db = null;
                var armed = false;
                Exception other = null;
                var settings = new EngineSettings
                {
                    Filename = file.Filename,
                    CheckpointStage = stage => { if (armed && stage == "data-page") power.DataFails = true; },
                    ReopenStage = stage =>
                    {
                        if (!armed || stage != "stopped") return;
                        armed = false;
                        // After the failure's teardown, another call reopens the engine read-only.
                        var thread = new Thread(() => other = Record.Exception(() => SyncPowerLossModel.AssertRows(db, Rows, 3)));
                        thread.Start();
                        thread.Join();
                    }
                };
                using (db = new LiteDatabase(new LiteEngine(settings)))
                {
                    db.CheckpointSize = 0;
                    for (var value = 1; value <= 2; value++) Update(db, value);
                    db.CheckpointSize = 1;
                    armed = true;
                    Action commit = () => Update(db, 3); // its automatic checkpoint fails after writing a page
                    commit.Should().NotThrow("the commit succeeded: its checkpoint's failure is recorded, not thrown");
                    power.DataFails.Should().BeTrue("the checkpoint wrote a page");
                    other.Should().BeNull("the other call read the reopened engine");

                    SyncPowerLossModel.AssertRows(db, Rows, 3);
                    var record = ReadOnlyAfterWriteFailure.AssertReported(db, "A checkpoint", "data", "The data file stopped syncing");
                    ReadOnlyAfterWriteFailure.AssertWriteRefused(() => Update(db, 4), record);
                }
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// Dispose did not wait for a reopen in progress: it closed the failed engine, and the reopen then
        /// published new services that kept the streams open and served calls after Dispose. Dispose now
        /// waits for the reopen and closes what it opened.
        /// </summary>
        [Fact]
        public void Dispose_while_the_engine_reopens_closes_the_reopened_engine()
        {
            using var reopening = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            var armed = false;
            var (engine, rows) = FailedEngine(stage =>
            {
                if (!armed || stage != "before-open") return;
                armed = false;
                reopening.Set();
                proceed.Wait(10000);
            });
            try
            {
                armed = true;
                var reopen = Task.Run(() => Record.Exception(() => rows.Count()));
                reopening.Wait(10000).Should().BeTrue();
                var dispose = Task.Run(engine.Dispose);
                dispose.Wait(300).Should().BeFalse("Dispose waits for the reopen that already started");
                proceed.Set();
                dispose.Wait(10000).Should().BeTrue();
                reopen.Wait(10000).Should().BeTrue();

                Action read = () => rows.Count();
                read.Should().Throw<LiteException>("the reopened engine was closed too")
                    .Which.ErrorCode.Should().Be(LiteException.ENGINE_DISPOSED);
            }
            finally
            {
                proceed.Set();
                engine.Dispose();
            }
        }

        /// <summary>
        /// A reopen publishes its new state before its services. A call that arrived in between passed
        /// the state check and used the failed engine's disposed services (or none). It now waits until
        /// the reopen finished, and reads the rows.
        /// </summary>
        [Fact]
        public void Call_that_arrives_while_the_engine_reopens_waits_for_it()
        {
            using var published = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            var armed = false;
            var (engine, rows) = FailedEngine(stage =>
            {
                if (!armed || stage != "state-published") return;
                armed = false;
                published.Set();
                proceed.Wait(10000);
            });
            try
            {
                armed = true;
                var reopen = Task.Run(() => rows.Count());
                published.Wait(10000).Should().BeTrue();
                int[] seen = null;
                var call = Task.Run(() => Record.Exception(() => seen = rows.FindAll().Select(x => x["_id"].AsInt32).ToArray()));
                call.Wait(300).Should().BeFalse("a call that arrives mid-reopen waits for the new services");
                proceed.Set();
                call.Wait(10000).Should().BeTrue();
                call.Result.Should().BeNull();
                seen.Should().Equal(1);
                reopen.Result.Should().Be(1);
            }
            finally
            {
                proceed.Set();
                engine.Dispose();
            }
        }

        /// <summary>
        /// After a recorded failure an explicit Checkpoint() returned 0 as if it had nothing to do: the
        /// caller's drain did not happen. It now throws the recorded failure, on a direct and on a shared
        /// connection. A database opened read-only by its caller still checkpoints nothing, silently.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Explicit_checkpoint_after_a_write_failure_throws_it(bool shared)
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            DurableLogs.Forget(Path.GetFullPath(logName));
            try
            {
                using (var power = new FilePowerLossModel(file.Filename) { LogFails = true })
                using (var db = new LiteDatabase($"Filename={file.Filename}" + (shared ? ";Connection=shared" : "")))
                {
                    Action insert = () => Update(db, 1);
                    insert.Should().Throw<IOException>().WithMessage("This commit was not written: the log file cannot sync*");
                    var record = ReadOnlyAfterWriteFailure.AssertReported(db, "A commit", "log", "This commit was not written", walKept: false);
                    ReadOnlyAfterWriteFailure.AssertWriteRefused(() => db.Checkpoint(), record);
                    SyncPowerLossModel.AssertRows(db, Rows, 0);
                }
                using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, ReadOnly = true }))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    engine.Checkpoint().Should().Be(0);
                    SyncPowerLossModel.AssertRows(db, Rows, 0);
                }
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// Lost transactions are kept by thread ID, which pool threads reuse. A thread whose explicit
        /// transaction a failure ended found BeginTrans return false (joining a transaction that was
        /// gone), and a later, unrelated Commit on that thread threw. BeginTrans now consumes the mark and
        /// throws the recorded failure; the next BeginTrans starts a transaction. A thread whose mark is
        /// still there rolls back with true.
        /// </summary>
        [Fact]
        public void Begin_on_a_thread_whose_transaction_a_failure_ended_throws_the_failure_once()
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            db.BeginTrans().Should().BeTrue();
            db.GetCollection("pending").Insert(new BsonDocument { ["_id"] = 1 });
            using var begun = new ManualResetEventSlim();
            using var failed = new ManualResetEventSlim();
            var rolledBack = false;
            var other = new Thread(() =>
            {
                db.BeginTrans();
                db.GetCollection("other").Insert(new BsonDocument { ["_id"] = 2 });
                begun.Set();
                failed.Wait(10000);
                rolledBack = db.Rollback();
            });
            other.Start();
            begun.Wait(10000).Should().BeTrue();
            OnFailingThread(engine, () => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 }));
            failed.Set();
            other.Join();
            rolledBack.Should().BeTrue("the failure ended that thread's transaction");

            Action begin = () => db.BeginTrans();
            var refused = begin.Should().Throw<IOException>().Which;
            refused.Message.Should().StartWith(LiteEngine.WriteFailedPrefix + "A commit failed at");
            refused.InnerException.Should().NotBeNull();
            db.BeginTrans().Should().BeTrue("the mark is consumed: an unrelated transaction starts");
            db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).Should().Equal(1);
            db.CollectionExists("pending").Should().BeFalse();
            db.CollectionExists("other").Should().BeFalse();
            db.Commit().Should().BeTrue();
        }

        /// <summary>
        /// An engine over memory streams whose commit failed (recorded); <paramref name="stage"/> watches
        /// its stop and read-only reopen. Returns the engine and its "rows" (id 1) for the next call.
        /// </summary>
        private static (LiteEngine, ILiteCollection<BsonDocument>) FailedEngine(Action<string> stage)
        {
            var engine = new LiteEngine(new EngineSettings { DataStream = new MemoryStream(), LogStream = new MemoryStream(), ReopenStage = stage });
            var db = new LiteDatabase(engine, disposeOnClose: false);
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1 });
            OnFailingThread(engine, () => rows.Insert(new BsonDocument { ["_id"] = 2 }));
            return (engine, rows);
        }

        /// <summary>Run <paramref name="write"/> on a thread whose WAL writes fail: its commit throws, and the failure is recorded.</summary>
        private static void OnFailingThread(LiteEngine engine, Action write)
        {
            Exception thrown = null;
            var failing = new Thread(() =>
            {
                engine.SimulateDiskWriteFail = _ =>
                {
                    if (Thread.CurrentThread.Name == "failing") throw new IOException("injected WAL write failure");
                };
                thrown = Record.Exception(write);
            }) { Name = "failing" };
            failing.Start();
            failing.Join();
            thrown.Should().BeOfType<IOException>().Which.Message.Should().Be("injected WAL write failure");
        }
    }
}
#endif
