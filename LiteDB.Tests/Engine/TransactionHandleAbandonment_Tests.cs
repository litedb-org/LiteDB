using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleAbandonment_Tests
    {
        // Shared only: its holder thread owns native writer ownership that finalization must release.
        // An abandoned Direct engine has no holder; it is the existing unclosed-database case.
        [Theory]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Completed_handle_does_not_root_abandoned_session_and_active_handle(bool shared, bool promote)
        {
            using var file = new TempFile();
            var completed = Abandon(file, shared, promote, out var references);
            for (var attempt = 0; attempt < 100 &&
                (references.Any(reference => reference.IsAlive) || Locked(file)); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(20);
            }
            Assert.All(references, reference => Assert.False(reference.IsAlive));
            Assert.False(Locked(file));
            Assert.Equal(LiteTransactionState.Committed, completed.State);
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var reopened = new LiteDatabase(file);
                Assert.Equal(1, reopened.GetCollection("rows").Count());
                Assert.NotNull(reopened.GetCollection("rows").FindOne("sentinel = 'preserved'"));
                Assert.Null(reopened.GetCollection("rows").FindById(2));
            }
            completed.Dispose();
            GC.KeepAlive(completed);
        }

        // Direct storage keeps its files open exclusively until its streams close.
        private static bool Locked(string file)
        {
            try
            {
                using var stream = new System.IO.FileStream(file, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
                return false;
            }
            catch (System.IO.IOException) { return true; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ILiteTransaction Abandon(string file, bool shared, bool promote, out WeakReference[] references)
        {
            LiteDatabase db = null;
            var connection = new ConnectionString { Filename = file, TransactionPageLimit = 1,
                Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };
            var engine = connection.CreateEngine(settings => settings.ReadTransform = (collection, value) =>
            { GC.KeepAlive(db); return value; });
            db = new LiteDatabase(engine);
            var completed = db.BeginTransaction();
            completed.GetCollection("rows").EnsureIndex("sentinel");
            completed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["sentinel"] = "preserved" });
            completed.Commit();
            if (promote) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            var abandoned = db.BeginTransaction();
            abandoned.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["payload"] = new string('x', 50000) });
            Assert.Equal(2, abandoned.GetCollection("rows").Count());
            references = new[] { new WeakReference(db), new WeakReference(abandoned), new WeakReference(engine) };
            return completed;
        }
    }
}
