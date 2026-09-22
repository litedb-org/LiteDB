using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2991_Tests
    {
        [Fact]
        public void Retired_reader_owner_does_not_make_new_threads_recursive_writers()
        {
            using var gate = new TransactionGate();
            for (var owner = 0; owner < 128; owner++) RunThread(() =>
            {
                gate.TryEnterReadLock(TimeSpan.Zero).Should().BeTrue();
            });
            for (var i = 0; i < 32; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                RunThread(() =>
                {
                    // A foreign checkpoint must wait for the live cursor,
                    // even if its originating thread has already terminated.
                    gate.IsReadLockHeld.Should().BeFalse("a new thread must not inherit a retired owner");
                    gate.TryEnterWriteLock(TimeSpan.Zero).Should().BeFalse();
                });
            }
        }

        [Fact]
        public void Retired_cursor_owners_remain_distinct_and_foreign_cleanup_releases_all_leases()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new[] { new BsonDocument { ["_id"] = 1 }, new BsonDocument { ["_id"] = 2 } });
            var cursors = new List<IEnumerator<BsonDocument>>();
            try
            {
                for (var i = 0; i < 64; i++) RunThread(() =>
                {
                    var cursor = rows.FindAll().GetEnumerator();
                    cursors.Add(cursor);
                    cursor.MoveNext().Should().BeTrue();
                });
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                for (var i = 0; i < 128; i++) RunThread(() =>
                    engine.GetMonitor().GetThreadTransaction().Should().BeNull());

                for (var i = 0; i < cursors.Count; i++)
                {
                    var cursor = cursors[i];
                    RunThread(() =>
                    {
                        db.BeginTrans().Should().BeTrue();
                        try
                        {
                            cursor.MoveNext().Should().BeTrue();
                            cursor.Current["_id"].AsInt32.Should().Be(2);
                            cursor.Dispose();
                            cursor.Dispose();
                            rows.Insert(new BsonDocument { ["_id"] = 3 });
                        }
                        finally { db.Rollback().Should().BeTrue(); }
                    });
                    engine.GetMonitor().Transactions.Count.Should().Be(cursors.Count - i - 1);
                }
                RunThread(db.Checkpoint);
                db.Checkpoint();
                rows.Count().Should().Be(2);
            }
            finally { foreach (var cursor in cursors) cursor.Dispose(); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunThread(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
