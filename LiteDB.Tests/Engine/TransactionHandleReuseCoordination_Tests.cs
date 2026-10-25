#if NET8_0_OR_GREATER
using System.IO;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;
using static LiteDB.Tests.Engine.SharedWaitGuards_Tests;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A reused Shared holder wrapper joins mapped read coordination exactly as a new wrapper would (#3083):
    /// it never creates an authority, joins an existing one for each handle and leaves it afterwards.
    /// </summary>
    [Collection(NativeFileSyncCollection.Name)]
    public class TransactionHandleReuseCoordination_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Reused_wrapper_participates_in_coordination_only_during_its_handle(string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            var page = SharedCoordinationFallback.PagePath(Path.GetFullPath(file));
            var shared = new SharedEngine(new EngineSettings { Filename = file, Password = password });
            using (var db = new LiteDatabase(shared))
            {
                using (var warm = db.BeginTransaction()) warm.Commit();
                var child = shared.CachedTransactionChild;
                Assert.NotNull(child);
                // No authority exists: a one-operation participant never creates one, however often reused.
                for (var id = 2; id <= 5; id++)
                {
                    using var tx = db.BeginTransaction();
                    tx.GetCollection("rows").Insert(Row(id));
                    Assert.Null(ReuseAccess.Field(child, "_coordination"));
                    Assert.False(File.Exists(page), "A reused wrapper created a mapped coordination authority.");
                    tx.Commit();
                }
                // Repeated ordinary reads of the connection create the authority.
                for (var read = 0; read < 3; read++) Assert.NotNull(db.GetCollection("rows").FindById(1));
                // Where mapped reads are unavailable (a recorded fallback) only the handles' outcome is checked.
                var authority = ReuseAccess.Field(shared, "_coordination") != null;
                if (!authority) Assert.NotNull(shared.CoordinationFallbackReason);
                for (var id = 6; id <= 8; id++)
                {
                    using (var tx = db.BeginTransaction())
                    {
                        tx.GetCollection("rows").Insert(Row(id));
                        // A writer must join the existing authority, so mapped readers learn of its commit.
                        if (authority) Assert.NotNull(ReuseAccess.Field(child, "_coordination"));
                        tx.Commit();
                    }
                    Assert.Same(child, shared.CachedTransactionChild);
                    Assert.Null(ReuseAccess.Field(child, "_coordination"));
                    Assert.Equal(0, (int)ReuseAccess.Field(child, "_coordinationDemand"));
                    // The connection's own reads, possibly mapped, see the handle's commit.
                    Assert.NotNull(db.GetCollection("rows").FindById(id));
                }
            }
            Verify(file, password, 1, 2, 3, 4, 5, 6, 7, 8);
        }
    }
}
#endif
