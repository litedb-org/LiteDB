using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Shared handles never let the holder's process identity stand in for an impersonating
    /// caller: begin and every later operation are refused there. Windows only; elsewhere the
    /// positive control shows that begin and operations are unaffected.
    /// </summary>
    public class TransactionHandleImpersonation_Tests
    {
        private static readonly bool Windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };
        private static ConnectionString Shared(TempFile file) => new ConnectionString { Filename = file, Connection = ConnectionType.Shared };

        private static Exception Impersonating(bool anonymous, Action action)
        {
            Exception failure = null;
            string setup = null;
            var thread = new Thread(() =>
            {
                if (!(anonymous ? ImpersonateAnonymousToken(GetCurrentThread()) : ImpersonateSelf(2 /* SecurityImpersonation */)))
                {
                    setup = "impersonation failed: " + Marshal.GetLastWin32Error();
                    return;
                }
                try { action(); }
                catch (Exception error) { failure = error; }
                finally { if (!RevertToSelf()) setup = "revert failed: " + Marshal.GetLastWin32Error(); }
            });
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
            Assert.Null(setup);
            return failure;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Begin_under_impersonation_is_refused_before_admission(bool anonymous)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(Shared(file));
            db.GetCollection("rows").Insert(Row(1));
            if (!Windows)
            {
                using (var tx = db.BeginTransaction()) { tx.GetCollection("rows").Insert(Row(2)); tx.Commit(); }
                Assert.Equal(2, db.GetCollection("rows").Count());
                return;
            }
            var error = Impersonating(anonymous, () => db.BeginTransaction().Dispose());
            Assert.IsType<NotSupportedException>(error);
            Assert.Equal(0, db.TransactionHandles.ActiveCount);
            // Nothing was admitted: the next begin and an ordinary write proceed at once.
            using (var tx = db.BeginTransaction()) { tx.GetCollection("rows").Insert(Row(2)); tx.Commit(); }
            db.GetCollection("rows").Insert(Row(3));
            Assert.Equal(new[] { 1, 2, 3 }, db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Operation_under_impersonation_is_refused_and_leaves_the_handle_usable(bool anonymous)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(Shared(file)))
            {
                db.GetCollection("rows").Insert(Row(1));
                using var tx = db.BeginTransaction();
                var rows = tx.GetCollection("rows");
                rows.Insert(Row(2));
                if (Windows)
                {
                    Assert.IsType<NotSupportedException>(Impersonating(anonymous, () => rows.Insert(Row(3))));
                    Assert.IsType<NotSupportedException>(Impersonating(anonymous, () => rows.Count()));
                    Assert.IsType<NotSupportedException>(Impersonating(anonymous, tx.Commit));
                    Assert.Equal(LiteTransactionState.Active, tx.State);
                }
                rows.Insert(Row(4));
                tx.Commit();
            }
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 1, 2, 4 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ImpersonateSelf(int level);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ImpersonateAnonymousToken(IntPtr thread);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RevertToSelf();
    }
}
