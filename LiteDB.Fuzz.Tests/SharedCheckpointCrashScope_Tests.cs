using System.Reflection;
using LiteDB.Engine;
using LiteDB.Fuzz.Targets;
using Xunit;

namespace LiteDB.Fuzz.Tests;

public sealed class SharedCheckpointCrashScope_Tests
{
    [Theory]
    [InlineData("checkpoint-before-clear", true)]
    [InlineData("checkpoint-after-clear", true)]
    [InlineData("wal-after-index-confirmation", false)]
    [InlineData(null, false)]
    public void Only_checkpoint_crash_workers_retain_the_native_mutex_after_commit(string phase, bool retained)
    {
        var root = Path.Combine(Path.GetTempPath(), "litedb-checkpoint-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "test.db");
            using var engine = new SharedEngine(new EngineSettings { Filename = file });
            using var db = new LiteDatabase(engine);
            // Observe the actual native lock from a separate thread, without a sleep or
            // a task-start deadline that could mistake an unscheduled peer for exclusion.
            var owner = typeof(SharedEngine).GetField("_owner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            var mutex = (Mutex)owner.GetType().GetProperty("Mutex")!.GetValue(owner)!;
            using (var scope = SharedCheckpointCrashScope.Create(engine, phase))
            {
                db.BeginTrans();
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
                db.Commit();
                owner.GetType().GetMethod("WaitForRelease")!.Invoke(owner, null);
                Assert.Equal(!retained, PeerCanEnter(mutex));
                db.Checkpoint();
                Assert.Equal(!retained, PeerCanEnter(mutex));
            }
            Assert.True(PeerCanEnter(mutex));
            using var reopened = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared });
            Assert.Equal(42, reopened.GetCollection("rows").FindById(1)["value"].AsInt32);
        }
        finally { Directory.Delete(root, true); }
    }

    private static bool PeerCanEnter(Mutex mutex)
    {
        var acquired = false;
        var peer = new Thread(() =>
        {
            acquired = mutex.WaitOne(0);
            if (acquired) mutex.ReleaseMutex();
        });
        peer.Start();
        Assert.True(peer.Join(TimeSpan.FromSeconds(5)));
        return acquired;
    }
}
