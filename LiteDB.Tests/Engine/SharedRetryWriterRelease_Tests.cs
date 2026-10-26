using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Safety;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedRetryWriterRelease_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "i3094-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public SharedRetryWriterRelease_Tests() => Directory.CreateDirectory(_directory);

        public static IEnumerable<object[]> Routes =>
            from encrypted in new[] { false, true }
            from pinned in new[] { false, true }
            select new object[] { encrypted, pinned };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Retry_after_duplicate_key_releases_writer_ownership(bool encrypted)
        {
            var connection = Connection(encrypted);
            using var db = new LiteDatabase(connection);
            var col = db.GetCollection("docs");
            col.Insert(Row(1));
            db.BeginTrans().Should().BeTrue();
            Action duplicate = () => col.Insert(Row(1));
            duplicate.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(110);
            db.BeginTrans().Should().BeTrue();
            db.Commit().Should().BeTrue();

            // The exact public issue repro, with the original owner thread still alive.
            OnThread(db, () => col.Count().Should().Be(1));
            OnThread(db, () =>
            {
                col.Insert(Row(2));
                using var peer = new LiteDatabase(connection);
                peer.GetCollection("docs").Insert(Row(3));
            });
            db.Dispose();
            Verify(encrypted, 1, 2, 3);
        }

        [Theory]
        [MemberData(nameof(Routes))]
        public void Repeated_retries_return_each_obsolete_hold_exactly_once(bool encrypted, bool pinned)
        {
            Seed(encrypted);
            using var protection = new OwnershipMonitor();
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename, Password = Password(encrypted) });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var anchor = Pin(engine, pinned);
            db.BeginTrans().Should().BeTrue();
            for (var retry = 0; retry < 3; retry++)
            {
                db.BeginTrans().Should().BeFalse();
                Ownership(engine, pinned).Should().Be(1, "a false join adds no ownership");
                db.GetCollection("docs").Insert(Row(1000 + retry));
                Action duplicate = () => db.GetCollection("docs").Insert(Row(1));
                duplicate.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(110);
                db.BeginTrans().Should().BeTrue();
                Ownership(engine, pinned).Should().Be(1, "the aborted transaction's ownership is replaced, never accumulated");
                // Returning a logical hold must not release the native mutex around the live core.
                protection.LiveCores(engine).Should().BeGreaterThan(0);
                protection.Evaluate(engine).Should().BeNull();
                protection.TakeAll().Should().BeEmpty();
            }
            db.GetCollection("docs").Insert(Row(2000));
            db.Commit().Should().BeTrue();
            Ownership(engine, pinned).Should().Be(0);
            db.Commit().Should().BeFalse();
            db.Rollback().Should().BeFalse();
            OnThread(db, () =>
            {
                using var peer = new LiteDatabase(Connection(encrypted));
                peer.GetCollection("docs").Insert(Row(2001));
            });
            anchor?.Dispose();
            engine.Dispose();
            protection.LiveCores(engine).Should().Be(0);
            protection.TakeAll().Should().BeEmpty("native writer protection ends only after core teardown");
            Verify(encrypted, Enumerable.Range(1, 150).Concat(new[] { 2000, 2001 }).ToArray());
        }

        [Theory]
        [MemberData(nameof(Routes))]
        public void Failed_retry_preserves_old_ownership_until_retry_or_rollback(bool encrypted, bool pinned)
        {
            Seed(encrypted);
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename, Password = Password(encrypted) });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var anchor = Pin(engine, pinned);
            db.BeginTrans().Should().BeTrue();
            Action duplicate = () => db.GetCollection("docs").Insert(Row(1));
            duplicate.Should().Throw<LiteException>();
            var monitor = Field<TransactionMonitor>(Field<LiteEngine>(engine, "_engine"), "_monitor");
            monitor.BeforeTransactionRegistration = () => throw new IOException("retry begin failure");
            try
            {
                Action retry = () => db.BeginTrans();
                retry.Should().Throw<IOException>().WithMessage("retry begin failure");
                Ownership(engine, pinned).Should().Be(1, "failed Begin owes no new completion");
            }
            finally { monitor.BeforeTransactionRegistration = null; }
            db.BeginTrans().Should().BeTrue();
            Ownership(engine, pinned).Should().Be(1);
            duplicate.Should().Throw<LiteException>();
            db.Rollback().Should().BeFalse("the inner transaction already aborted");
            Ownership(engine, pinned).Should().Be(0);
            db.Rollback().Should().BeFalse();
            db.BeginTrans().Should().BeTrue();
            db.GetCollection("docs").Insert(Row(2000));
            db.Rollback().Should().BeTrue();
            OnThread(db, () => db.GetCollection("docs").Insert(Row(2001)));
            anchor?.Dispose();
            engine.Dispose();
            Verify(encrypted, Enumerable.Range(1, 150).Concat(new[] { 2001 }).ToArray());
        }

        [Theory]
        [MemberData(nameof(Routes))]
        public void Ordinary_transaction_keeps_guards_and_releases_on_completion(bool encrypted, bool pinned)
        {
            Seed(encrypted);
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename, Password = Password(encrypted) });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var anchor = Pin(engine, pinned);
            db.BeginTrans().Should().BeTrue();
            db.BeginTrans().Should().BeFalse();
            OnThread(db, () =>
            {
                Action commit = () => db.Commit();
                commit.Should().Throw<LiteException>().WithMessage("*same thread*");
                db.Rollback().Should().BeFalse();
            });
            Ownership(engine, pinned).Should().Be(1);
            db.GetCollection("docs").Insert(Row(2000));
            db.Commit().Should().BeTrue();
            Ownership(engine, pinned).Should().Be(0);
            OnThread(db, () => db.GetCollection("docs").Insert(Row(2001)));
            anchor?.Dispose();
            engine.Dispose();
            Verify(encrypted, Enumerable.Range(1, 150).Concat(new[] { 2000, 2001 }).ToArray());
        }

        [Theory]
        [MemberData(nameof(Routes))]
        public void Retry_retains_same_connection_recursion_and_peer_callback_guard(bool encrypted, bool pinned)
        {
            Seed(encrypted);
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename, Password = Password(encrypted) });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var peer = new LiteDatabase(Connection(encrypted));
            using var anchor = Pin(engine, pinned);
            db.BeginTrans().Should().BeTrue();
            Action duplicate = () => db.GetCollection("docs").Insert(Row(1));
            duplicate.Should().Throw<LiteException>();
            db.BeginTrans().Should().BeTrue();
            IEnumerable<BsonDocument> Input()
            {
                yield return Row(2000);
                db.BeginTrans().Should().BeFalse();
                db.GetCollection("docs").Insert(Row(2001));
                Action callback = () => peer.BeginTrans();
                callback.Should().Throw<InvalidOperationException>().WithMessage("*another connection*");
                yield return Row(2002);
            }
            db.GetCollection("docs").Insert(Input()).Should().Be(2);
            Ownership(engine, pinned).Should().Be(1);
            db.Commit().Should().BeTrue();
            OnThread(db, () => peer.GetCollection("docs").Insert(Row(2003)));
            anchor?.Dispose();
            // Cold Direct verification requires every Shared handle cache to be closed.
            peer.Dispose();
            engine.Dispose();
            Verify(encrypted, Enumerable.Range(1, 150).Concat(Enumerable.Range(2000, 4)).ToArray());
        }

#if !NETFRAMEWORK
        [Theory]
        [MemberData(nameof(Routes))]
        public void Child_process_can_write_after_retry_commit(bool encrypted, bool pinned)
        {
            Seed(encrypted);
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename, Password = Password(encrypted) });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var anchor = Pin(engine, pinned);
            db.BeginTrans().Should().BeTrue();
            Action duplicate = () => db.GetCollection("docs").Insert(Row(1));
            duplicate.Should().Throw<LiteException>();
            db.BeginTrans().Should().BeTrue();
            db.GetCollection("docs").Insert(Row(2000));
            db.Commit().Should().BeTrue();
            using (var child = new LiteDB.Internals.MvccProcess("insert", Filename, Password(encrypted), "3000"))
            {
                child.ReadLine(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Should().Be("done");
                child.Finish().GetAwaiter().GetResult();
            }
            anchor?.Dispose();
            engine.Dispose();
            Verify(encrypted, Enumerable.Range(1, 150).Concat(new[] { 2000 }).Concat(Enumerable.Range(3000, 20)).ToArray());
        }
#endif

        private static T Field<T>(object value, string name) =>
            (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);

        private static int Ownership(SharedEngine engine, bool pinned) => pinned
            ? Field<int>(Field<object>(engine, "_pin"), "_holds")
            : Field<int>(engine.MutexOwner, "_recursion");

        private static IBsonDataReader Pin(SharedEngine engine, bool pinned)
        {
            if (!pinned) return null;
            engine.PinIdleLimit = engine.PinHoldLimit = TimeSpan.FromMinutes(10);
            var reader = engine.Query("docs", new Query());
            reader.Read().Should().BeTrue();
            engine.Update("docs", new[] { Row(1) }).Should().Be(1);
            Field<object>(engine, "_pin").Should().NotBeNull();
            return reader;
        }

        private static void OnThread(LiteDatabase db, Action action)
        {
            Exception error = null;
            var thread = new Thread(() => error = Record.Exception(action)) { IsBackground = true };
            thread.Start();
            var completed = thread.Join(TimeSpan.FromSeconds(3));
            if (!completed)
            {
                db.Dispose(); // End the baseline's leaked ownership before failing the test.
                thread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            }
            completed.Should().BeTrue("another thread must proceed while the transaction's owner stays alive");
            error.Should().BeNull();
        }

        private ConnectionString Connection(bool encrypted) => new ConnectionString
        {
            Filename = Filename, Password = Password(encrypted), Connection = ConnectionType.Shared
        };

        private static string Password(bool encrypted) => encrypted ? "secret" : null;
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id };

        private void Seed(bool encrypted)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = Filename, Password = Password(encrypted) });
            db.GetCollection("docs").Insert(Enumerable.Range(1, 150).Select(Row));
            db.GetCollection("docs").EnsureIndex("value");
            db.GetCollection("sentinel").Insert(Row(42));
        }

        private void Verify(bool encrypted, params int[] ids)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = Filename, Password = Password(encrypted) });
            var rows = db.GetCollection("docs");
            rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(ids);
            foreach (var id in ids)
            {
                rows.FindById(id)["_id"].AsInt32.Should().Be(id);
                if (id < 3000) rows.FindOne(Query.EQ("value", id))["_id"].AsInt32.Should().Be(id);
            }
            if (ids.Length > 3) db.GetCollection("sentinel").FindById(42)["value"].AsInt32.Should().Be(42);
        }

        public void Dispose() => WindowsFileRelease.DeleteDirectory(_directory);
    }
}
