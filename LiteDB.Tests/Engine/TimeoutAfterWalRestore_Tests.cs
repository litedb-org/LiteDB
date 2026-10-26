using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TimeoutAfterWalRestore_Tests : IDisposable
    {
        public enum Opening { Clean, WalOnly, SetterAfterWal }
        public enum Contention { Collection, Admission, Exclusive }

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "i3099-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        private string LogFilename => FileHelper.GetLogFile(Filename);

        public TimeoutAfterWalRestore_Tests() => Directory.CreateDirectory(_directory);

        public static IEnumerable<object[]> Cases =>
            from opening in new[] { Opening.Clean, Opening.WalOnly, Opening.SetterAfterWal }
            from contention in new[] { Contention.Collection, Contention.Admission, Contention.Exclusive }
            from encrypted in new[] { false, true }
            select new object[] { opening, contention, encrypted };

        [Theory]
        [MemberData(nameof(Cases))]
        public void Contended_locks_use_the_current_timeout(Opening opening, Contention contention, bool encrypted)
        {
            Seed(opening, encrypted);
            using var engine = new LiteEngine(Settings(encrypted));
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            if (opening != Opening.WalOnly) db.Timeout = TimeSpan.FromSeconds(1);
            db.Timeout.Should().Be(TimeSpan.FromSeconds(1));
            Contend(engine, db, contention, db.Timeout);
            Verify(db, 1);
            db.GetCollection("docs").Insert(Row(4));
            engine.Checkpoint().Should().BeGreaterThan(0);
            engine.Dispose();
            using var reopened = new LiteDatabase(new ConnectionString { Filename = Filename, Password = encrypted ? "secret" : null });
            reopened.Timeout.Should().Be(TimeSpan.FromSeconds(1));
            Verify(reopened, 1, 4);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Setter_and_checkpoint_reopen_keep_using_the_live_pragmas(bool encrypted)
        {
            Seed(Opening.WalOnly, encrypted);
            using (var engine = new LiteEngine(Settings(encrypted)))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Contend(engine, db, Contention.Collection, TimeSpan.FromSeconds(1));
                db.Timeout = TimeSpan.FromSeconds(2);
                Contend(engine, db, Contention.Collection, TimeSpan.FromSeconds(2));
                engine.Checkpoint().Should().BeGreaterThan(0);
                new FileInfo(LogFilename).Length.Should().Be(encrypted ? Constants.PAGE_SIZE : 0,
                    "an empty encrypted WAL retains only its encryption header");
                Verify(db, 1);
            }
            using var reopenedEngine = new LiteEngine(Settings(encrypted));
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.Timeout.Should().Be(TimeSpan.FromSeconds(2));
            Contend(reopenedEngine, reopened, Contention.Admission, TimeSpan.FromSeconds(2));
            Verify(reopened, 1);
        }

        private static void Contend(LiteEngine engine, LiteDatabase db, Contention contention, TimeSpan timeout)
        {
            var locker = (LockService)typeof(LiteEngine).GetField("_locker", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            if (contention == Contention.Admission) locker.EnterExclusive().Should().BeTrue();
            else
            {
                db.BeginTrans().Should().BeTrue();
                db.GetCollection("docs").Insert(Row(2));
            }

            using var attempting = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            Exception failure = null;
            TimeSpan elapsed = default;
            var worker = new Thread(() =>
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    attempting.Set();
                    if (contention == Contention.Exclusive)
                    {
                        if (locker.EnterExclusive()) locker.ExitExclusive();
                    }
                    else
                    {
                        db.BeginTrans().Should().BeTrue();
                        db.GetCollection("docs").Insert(Row(3));
                    }
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    elapsed = clock.Elapsed;
                    if (contention != Contention.Exclusive) db.Rollback();
                    finished.Set();
                }
            }) { IsBackground = true };
            worker.Start();
            bool completed;
            try
            {
                attempting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the contender must actually run");
                // Release the holder after four seconds even on the 60-second baseline.
                // A stale timeout then succeeds instead of timing out, and fails below.
                completed = finished.Wait(TimeSpan.FromSeconds(4));
            }
            finally
            {
                if (contention == Contention.Admission) locker.ExitExclusive();
                else db.Rollback().Should().BeTrue();
                worker.Join(TimeSpan.FromSeconds(5)).Should().BeTrue("cleanup must drain the real lock waiter");
            }
            completed.Should().BeTrue("TIMEOUT must expire before the bounded holder releases");
            failure.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.LOCK_TIMEOUT);
            failure.Message.Should().Contain(timeout.ToString());
            elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(timeout.TotalMilliseconds * 0.8));
            elapsed.Should().BeLessThan(timeout + TimeSpan.FromSeconds(1.5));
        }

        private EngineSettings Settings(bool encrypted) => new EngineSettings
        {
            Filename = Filename, Password = encrypted ? "secret" : null
        };

        private void Seed(Opening opening, bool encrypted)
        {
            using (var db = new LiteDatabase(new ConnectionString { Filename = Filename, Password = encrypted ? "secret" : null }))
            {
                db.GetCollection("docs").Insert(Row(1));
                db.GetCollection("docs").EnsureIndex("value");
                db.GetCollection("sentinel").Insert(Row(42));
                db.Checkpoint(); // The data-file header still has the default 60-second timeout.
                if (opening != Opening.Clean)
                {
                    db.CheckpointSize = 0;
                    db.Timeout = TimeSpan.FromSeconds(opening == Opening.WalOnly ? 1 : 2);
                }
            }
            if (opening == Opening.Clean) File.Exists(LogFilename).Should().BeFalse();
            else new FileInfo(LogFilename).Length.Should().BeGreaterThan(0, "the header must be restored from a real WAL");
        }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id };

        private static void Verify(LiteDatabase db, params int[] ids)
        {
            var rows = db.GetCollection("docs");
            rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(ids);
            foreach (var id in ids) rows.FindOne(Query.EQ("value", id))["_id"].AsInt32.Should().Be(id);
            db.GetCollection("sentinel").FindById(42)["value"].AsInt32.Should().Be(42);
        }

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
