#if NET8_0_OR_GREATER
using System;
using System.Runtime.InteropServices;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class TransactionHandleSharedIdentity_Tests
    {
        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public void Caller_identity_policy_precedes_admission_and_preserves_the_existing_handle(bool mapped)
        {
            using var file = new TempFile();
            using (var engine = new SharedEngine(new EngineSettings { Filename = file, SharedOneFile = mapped }))
            using (var db = new LiteDatabase(engine))
            using (var tx = db.BeginTransaction())
            {
                tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Assert.True(OpenProcessToken(new IntPtr(-1), 0x000A, out var processToken));
                    IntPtr impersonation = IntPtr.Zero;
                    try
                    {
                        Assert.True(DuplicateToken(processToken, 2, out impersonation));
                        Assert.True(ImpersonateLoggedOnUser(impersonation));
                        try
                        {
                            Assert.Throws<NotSupportedException>(() => db.BeginTransaction());
                            Assert.Throws<NotSupportedException>(() => tx.GetCollection("rows").Count());
                            Assert.Throws<NotSupportedException>(tx.Commit);
                            Assert.Equal(LiteTransactionState.Active, tx.State);
                            Assert.Equal(1, db.TransactionHandles.ActiveCount);
                        }
                        finally { Assert.True(RevertToSelf()); }
                    }
                    finally
                    {
                        if (impersonation != IntPtr.Zero) CloseHandle(impersonation);
                        CloseHandle(processToken);
                    }
                }
                else TransactionHolderContext.Validate();
                Assert.Equal(1, tx.GetCollection("rows").Count());
                tx.Commit();
            }
            using var cold = new LiteDatabase(file);
            Assert.NotNull(cold.GetCollection("rows").FindById(1));
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateToken(IntPtr token, int level, out IntPtr duplicate);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ImpersonateLoggedOnUser(IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RevertToSelf();
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
#endif
