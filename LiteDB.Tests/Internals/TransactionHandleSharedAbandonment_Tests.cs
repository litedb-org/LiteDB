using System;
using System.Runtime.CompilerServices;
using System.Threading;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class TransactionHandleSharedAbandonment_Tests
    {
#if NET8_0_OR_GREATER
        [OneFileTheory]
        [InlineData(true)]
        public void Mapped_Holder_context_does_not_root_abandoned_facade_or_open_handle(bool mapped) => Holder_context_does_not_root_abandoned_facade_or_open_handle(mapped);
#endif

        [Theory]
        [InlineData(false)]
        public void Holder_context_does_not_root_abandoned_facade_or_open_handle(bool mapped)
        {
            using var file = new TempFile();
            var completed = Abandon(file, mapped, out var references);
            var retired = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                if (Array.TrueForAll(references, weak => !weak.IsAlive))
                {
                    try { using var probe = new LiteDatabase(file); retired = true; break; }
                    catch (System.IO.IOException) { }
                }
                Thread.Sleep(20);
            }
            Assert.True(retired, "The holder rooted the caller graph or failed to retire native admission.");
            Assert.All(references, weak => Assert.False(weak.IsAlive));
            Assert.Equal(LiteTransactionState.Committed, completed.State);
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(file);
                Assert.NotNull(cold.GetCollection("rows").FindById(1));
                Assert.Null(cold.GetCollection("rows").FindById(2));
            }
            GC.KeepAlive(completed);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ILiteTransaction Abandon(string file, bool mapped, out WeakReference[] references)
        {
            LiteDatabase db = null;
            var engine = new SharedEngine(new EngineSettings { Filename = file, SharedOneFile = mapped,
                ReadTransform = (collection, value) => { GC.KeepAlive(db); return value; } });
            db = new LiteDatabase(engine);
            var ambient = new AsyncLocal<object>();
            ambient.Value = db;
            var completed = db.BeginTransaction();
            completed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            completed.Commit();
            var active = db.BeginTransaction();
            active.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
            references = new[] { new WeakReference(db), new WeakReference(engine), new WeakReference(active) };
            ambient.Value = null;
            return completed;
        }
    }
}
