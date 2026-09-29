#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// #3052: the failure boundary of the sync helpers that run after an earlier admission check
    /// (<c>$database.walKept</c>'s LogSyncs and DataFileSyncs, a checkpoint's syncs). A real I/O failure
    /// of a helper's sync is recorded before the helper releases the lock that orders the sync, and a
    /// helper, a checkpoint or a WAL batch that waited for that lock rechecks once it holds it: nothing
    /// more is written or synced through handles whose failure was published. Storage that answers
    /// "cannot sync" (#2242) is no failure and keeps working. Every interleaving is forced through test
    /// hooks and observed thread states; no timing is assumed.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class SyncFailureBoundary_Tests
    {
        private const int EIO = 5;
        private const int EINVAL = 22;

        /// <summary>
        /// Interleaving 1: reader A passed <c>$database</c>'s admission (no failure recorded) and is about to
        /// take the lock that orders its sync. Operation B (another <c>$database</c> read) syncs first, gets
        /// EIO, records it and releases the lock. A then syncs nothing: it reports the recorded failure.
        /// Without the failure, A syncs as before (positive control).
        /// </summary>
        [Theory]
        [InlineData("log", true)]
        [InlineData("log", false)]
        [InlineData("data", true)]
        [InlineData("data", false)]
        public void Waiting_sync_after_a_published_failure_syncs_nothing(string file, bool fail)
        {
            using var tmp = new TempFile();
            Setup(tmp.Filename);
            using var syncs = new SyncCounter(tmp.Filename);
            var data = Read(tmp.Filename);
            var log = Read(syncs.LogPath);
            using (var engine = new LiteEngine(new EngineSettings { Filename = tmp.Filename }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                if (file == "data") DataCannotSyncOnce(db, syncs);
                var before = syncs.Count(file);
                var aAdmitted = new ManualResetEventSlim();
                var bDone = new ManualResetEventSlim();
                Thread reader = null;
                engine.SimulateBeforeSyncLock = helper =>
                {
                    if (helper != file || Thread.CurrentThread != reader) return;
                    aAdmitted.Set();
                    bDone.Wait();
                };
                BsonDocument aInfo = null;
                reader = Start(() => aInfo = Info(db));
                aAdmitted.Wait();

                // B: the same read on this thread, whose sync fails (or succeeds) under the lock.
                syncs.Fail(file, fail ? EIO : 0);
                var bInfo = Info(db);
                syncs.Fail(file, 0);
                bInfo["walKept"].AsBoolean.Should().Be(fail, "a failed sync keeps the WAL; syncs that succeed keep nothing");
                bDone.Set();
                Join(reader);

                if (fail)
                {
                    (syncs.Count(file) - before).Should().Be(1, "only B synced: A found B's record once it held the lock");
                    aInfo["walKept"].AsBoolean.Should().BeTrue("A reports the recorded failure's kept WAL");
                    var failure = engine.GetState().WriteFailure;
                    failure.Should().NotBeNull();
                    failure.File.Should().Be(file);
                    failure.Operation.Should().Be(file == "log" ? "A log sync" : "A data sync");
                    engine.GetState().StopDue.Should().BeTrue("a read cannot stop the engine; its next call does");

                    var write = () => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1000 });
                    var syncsBefore = syncs.Count("log") + syncs.Count("data");
                    write.Should().Throw<IOException>().Which.ToString().Should().Contain("(errno 5)");
                    (syncs.Count("log") + syncs.Count("data")).Should().Be(syncsBefore, "the refused write synced nothing");
                }
                else
                {
                    (syncs.Count(file) - before).Should().Be(2, "without a failure both syncs run");
                    engine.GetState().WriteFailure.Should().BeNull();
                }
            }
            if (fail)
            {
                Read(tmp.Filename).Should().Equal(data, "nothing was written after the failure");
                Read(syncs.LogPath).Should().Equal(log, "no frame, journal or truncation followed the failure");
            }
            using var reopened = new LiteDatabase(tmp.Filename);
            reopened.GetCollection("rows").Count().Should().Be(Rows);
            (reopened.GetCollection("rows").FindById(1000) == null).Should().BeTrue();
        }

        /// <summary>
        /// Interleaving 2: the helper's own sync fails while a writer waits for the lock it holds. The
        /// writer was admitted before the failure (the engine was healthy) and is blocked on the WAL
        /// writer; the failure is published before the helper releases it, so the writer's recheck under
        /// the writer refuses it: no WAL frame, no acknowledgement. Without the failure it commits.
        /// </summary>
        [Theory]
        [InlineData("log", true)]
        [InlineData("log", false)]
        [InlineData("data", true)]
        [InlineData("data", false)]
        public void Helper_failure_is_published_before_a_waiting_writer_proceeds(string file, bool fail)
        {
            using var tmp = new TempFile();
            Setup(tmp.Filename);
            using var syncs = new SyncCounter(tmp.Filename);
            var log = Read(syncs.LogPath);
            Exception refused = null;
            using (var engine = new LiteEngine(new EngineSettings { Filename = tmp.Filename }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                if (file == "data") Info(db)["walKept"].AsBoolean.Should().BeFalse("its log sync creates the WAL writer, under which the data sync then runs");
                log = Read(syncs.LogPath);
                Thread writer = null;
                syncs.During(file, () =>
                {
                    // Inside the helper's sync, under its lock: a writer starts and blocks on the WAL writer.
                    writer = Start(() =>
                    {
                        try { db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1000 }); }
                        catch (Exception ex) { refused = ex; }
                    });
                    WaitBlocked(writer);
                    return fail ? EIO : 0;
                });
                // The helper: a sync of its own, here without a $database read around it (a read would
                // race with the refused writer's stop of the engine).
                var disk = engine.GetDisk();
                Func<bool> helper = file == "log" ? () => disk.LogSyncs() : () => disk.DataFileSyncs();
                if (fail) helper.Should().Throw<IOException>().Which.Message.Should().Contain("(errno 5)");
                else helper().Should().BeTrue();
                syncs.During(file, null);
                Join(writer);

                if (fail)
                {
                    refused.Should().BeAssignableTo<IOException>("the failure was visible when the writer got the lock");
                    refused.ToString().Should().Contain("(errno 5)");
                    engine.GetState().WriteFailure.File.Should().Be(file);
                }
                else
                {
                    refused.Should().BeNull();
                    (db.GetCollection("rows").FindById(1000) != null).Should().BeTrue();
                }
            }
            if (fail) Read(syncs.LogPath).Should().Equal(log, "the refused writer wrote no frame");
            using var reopened = new LiteDatabase(tmp.Filename);
            reopened.GetCollection("rows").Count().Should().Be(fail ? Rows : Rows + 1);
        }

        /// <summary>
        /// A checkpoint passed its admission check and waits for its locks; meanwhile another operation's
        /// log sync fails and is published under the WAL writer. The checkpoint's recheck under the writer
        /// refuses it before any sync or write: data file and WAL unchanged. Without the failure it drains
        /// the WAL.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Checkpoint_waiting_for_the_writer_rechecks_after_a_published_failure(bool fail)
        {
            using var tmp = new TempFile();
            Setup(tmp.Filename);
            using var syncs = new SyncCounter(tmp.Filename);
            var data = Read(tmp.Filename);
            var log = Read(syncs.LogPath);
            using (var engine = new LiteEngine(new EngineSettings { Filename = tmp.Filename }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var disk = engine.GetDisk();
                var dataSyncs = 0;
                engine.CheckpointStage = stage =>
                {
                    if (stage != "before-index-lock") return;
                    // Another operation that needs only the WAL writer: its log sync fails there.
                    syncs.Fail("log", fail ? EIO : 0);
                    Join(Start(() =>
                    {
                        try { disk.LogSyncs(); }
                        catch (IOException) when (fail) { }
                    }));
                    syncs.Fail("log", 0);
                    dataSyncs = syncs.Count("data");
                };
                Action checkpoint = () => engine.Checkpoint();
                if (fail)
                {
                    checkpoint.Should().Throw<IOException>().Which.ToString().Should().Contain("(errno 5)");
                    syncs.Count("data").Should().Be(dataSyncs, "the refused checkpoint synced nothing");
                }
                else
                {
                    checkpoint();
                    new FileInfo(syncs.LogPath).Length.Should().Be(0, "the checkpoint drained the WAL");
                }
            }
            if (fail)
            {
                Read(tmp.Filename).Should().Equal(data);
                Read(syncs.LogPath).Should().Equal(log);
            }
            using var reopened = new LiteDatabase(tmp.Filename);
            reopened.GetCollection("rows").Count().Should().Be(Rows);
        }

        /// <summary>
        /// Control (#2242): a log or data file that answers "cannot sync" is no failure. The waiting helper
        /// retries its sync, nothing is recorded, and writes go on.
        /// </summary>
        [Theory]
        [InlineData("log")]
        [InlineData("data")]
        public void Cannot_sync_answer_is_not_published_as_a_failure(string file)
        {
            using var tmp = new TempFile();
            Setup(tmp.Filename);
            using var syncs = new SyncCounter(tmp.Filename);
            using var engine = new LiteEngine(new EngineSettings { Filename = tmp.Filename, DurableCommits = false });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var before = syncs.Count(file);
            syncs.Fail(file, EINVAL);
            Info(db)["walKept"].AsBoolean.Should().BeTrue("storage that cannot sync keeps the WAL");
            Info(db)["walKept"].AsBoolean.Should().BeTrue();
            (syncs.Count(file) - before).Should().Be(2, "each read retried its sync: nothing was published");
            engine.GetState().WriteFailure.Should().BeNull("\"cannot sync\" is not an I/O failure");
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1000 });
            syncs.Fail(file, 0);
            db.GetCollection("rows").Count().Should().Be(Rows + 1);
        }

        private const int Rows = 20;

        /// <summary>Rows in the WAL of a database whose checkpoint pragma is 0: every open finds the WAL.</summary>
        private static void Setup(string filename)
        {
            using var db = new LiteDatabase(filename);
            db.CheckpointSize = 0;
            db.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => new BsonDocument { ["_id"] = id, ["v"] = new string('x', 500) }));
            new FileInfo(FileHelper.GetLogFile(filename)).Length.Should().BeGreaterThan(0);
        }

        /// <summary>
        /// The log syncs and the data file answers "cannot sync" once: the next <c>$database</c> reads skip
        /// the log sync and retry the data sync, where the interleaving meets.
        /// </summary>
        private static void DataCannotSyncOnce(LiteDatabase db, SyncCounter syncs)
        {
            syncs.Fail("data", EINVAL);
            Info(db)["walKept"].AsBoolean.Should().BeTrue();
            syncs.Fail("data", 0);
        }

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();

        private static byte[] Read(string filename)
        {
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadFully(bytes, 0, bytes.Length);
            return bytes;
        }

        private static ExceptionDispatchInfo _threadFailure;

        private static Thread Start(Action action)
        {
            _threadFailure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { _threadFailure = ExceptionDispatchInfo.Capture(ex); }
            });
            thread.IsBackground = true;
            thread.Start();
            return thread;
        }

        private static void Join(Thread thread)
        {
            thread.Join(TimeSpan.FromSeconds(60)).Should().BeTrue("the forced interleaving completes");
            _threadFailure?.Throw();
        }

        /// <summary>Observe the thread blocked (waiting for the lock the caller holds).</summary>
        private static void WaitBlocked(Thread thread)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while ((thread.ThreadState & ThreadState.WaitSleepJoin) == 0)
            {
                DateTime.UtcNow.Should().BeBefore(deadline, "the writer reaches the WAL writer's lock");
                Thread.Yield();
            }
        }

        /// <summary>Counts and fails device syncs of one database's data file and WAL (NativeFileSync hook).</summary>
        private sealed class SyncCounter : IDisposable
        {
            private readonly string _data;
            private int _dataSyncs, _logSyncs, _dataErrno, _logErrno;
            private Func<int> _dataDuring, _logDuring;

            internal SyncCounter(string filename)
            {
                _data = Path.GetFullPath(filename);
                LogPath = Path.GetFullPath(FileHelper.GetLogFile(filename));
                NativeFileSync.SimulateErrno = path =>
                {
                    var name = Path.GetFullPath(path);
                    if (string.Equals(name, LogPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref _logSyncs);
                        return Interlocked.Exchange(ref _logDuring, null)?.Invoke() ?? Volatile.Read(ref _logErrno);
                    }
                    if (string.Equals(name, _data, StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref _dataSyncs);
                        return Interlocked.Exchange(ref _dataDuring, null)?.Invoke() ?? Volatile.Read(ref _dataErrno);
                    }
                    return 0;
                };
            }

            internal string LogPath { get; }

            internal int Count(string file) => file == "log" ? Volatile.Read(ref _logSyncs) : Volatile.Read(ref _dataSyncs);

            internal void Fail(string file, int errno)
            {
                if (file == "log") Volatile.Write(ref _logErrno, errno);
                else Volatile.Write(ref _dataErrno, errno);
            }

            /// <summary>Run <paramref name="during"/> inside the next sync of <paramref name="file"/> (once); it returns the errno.</summary>
            internal void During(string file, Func<int> during)
            {
                if (file == "log") Volatile.Write(ref _logDuring, during);
                else Volatile.Write(ref _dataDuring, during);
            }

            public void Dispose() => NativeFileSync.SimulateErrno = null;
        }
    }
}
#endif
