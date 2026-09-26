using System.Reflection;
using LiteDB.Fuzz.Targets;
using Xunit;

namespace LiteDB.Fuzz.Tests;

[Collection("Shared checkpoint hook")]
public sealed class SharedCheckpointBoundary_Tests
{
    [Theory]
    [InlineData(21, "checkpoint-before-page-write")]
    [InlineData(22, "checkpoint-after-page-write")]
    [InlineData(23, "checkpoint-before-data-flush")]
    [InlineData(24, "checkpoint-after-data-flush")]
    [InlineData(25, "checkpoint-before-clear")]
    [InlineData(26, "checkpoint-after-clear")]
    public void Targeted_checkpoint_phase_runs_before_a_peer_can_drain_the_committed_WAL(int position, string phase)
    {
        var directory = Path.Combine(Path.GetTempPath(), "litedb-crash-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connection = new ConnectionString
            {
                Filename = Path.Combine(directory, "test.db"), Connection = ConnectionType.Shared
            };
            using var db = new LiteDatabase(connection);
            SharedProcessFuzzer.ConfigureCrashCheckpoint(db, position);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "control" });
            using var peer = new LiteDatabase(connection);
            var crashHook = typeof(LiteDatabase).Assembly.GetType("LiteDB.Engine.EngineState", true)
                .GetField("SimulateProcessCrash", BindingFlags.Static | BindingFlags.NonPublic);
            var observed = false;
            db.BeginTrans();
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "committed" });
            crashHook.SetValue(null, (Action<string>)(reached => { if (reached == phase) observed = true; }));
            try
            {
                db.Commit();
                Assert.True(observed, "The crash phase must execute while Commit still owns the writer mutex.");
                crashHook.SetValue(null, null);
                // This is the scheduling window that formerly made the crash child miss its hook.
                peer.Checkpoint();
                db.Checkpoint();
                Assert.Equal("committed", peer.GetCollection("rows").FindById(2)["value"].AsString);
                Assert.Equal("control", peer.GetCollection("rows").FindById(1)["value"].AsString);
            }
            finally { crashHook.SetValue(null, null); }
        }
        finally { Directory.Delete(directory, true); }
    }
}
