using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;

namespace LiteDB.Tests.Engine
{
    internal sealed class BackgroundCheckpointIntegrationScenario : IDisposable
    {
        internal readonly TempFile File = new TempFile();
        internal readonly LiteEngine Engine;
        internal readonly LiteDatabase Database;
        internal readonly string Password;
        internal DiskService Disk => DiskOf(Engine);
        internal AutomaticCheckpointCoordinator Worker => Disk.AutomaticCheckpoint;

        internal BackgroundCheckpointIntegrationScenario(string password = null, bool durable = false,
            int checkpoint = 2, int transactionPages = 1000)
        {
            Password = password;
            Engine = new LiteEngine(new EngineSettings
            {
                Filename = File.Filename, Password = password, DurableCommits = durable,
                TransactionPageLimit = transactionPages
            });
            Database = new LiteDatabase(Engine, disposeOnClose: false);
            Seed(Database, checkpoint);
        }

        internal static void Seed(LiteDatabase db, int checkpoint)
        {
            db.CheckpointSize = 0;
            db.GetCollection("rows").Insert(Enumerable.Range(0, 3).Select(BackgroundCheckpointScenario.Row));
            db.GetCollection("rows").EnsureIndex("value");
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "untouched" });
            db.CheckpointSize = checkpoint;
            db.Checkpoint();
        }

        internal static DiskService DiskOf(LiteEngine engine) => (DiskService)typeof(LiteEngine)
            .GetField("_disk", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);

        internal static void Verify(LiteDatabase db, int version)
        {
            db.UserVersion.Should().Be(version);
            var rows = db.GetCollection("rows");
            rows.FindAll().Should().BeEquivalentTo(Enumerable.Range(0, 3).Select(BackgroundCheckpointScenario.Row));
            foreach (var id in Enumerable.Range(0, 4))
                rows.Find(Query.EQ("value", id * 7)).Should().BeEquivalentTo(
                    id < 3 ? new[] { BackgroundCheckpointScenario.Row(id) } : Array.Empty<BsonDocument>());
            db.GetCollection("sentinel").FindById(1)["payload"].AsString.Should().Be("untouched");
        }

        internal void VerifyRecovery(int version)
        {
            Engine.Dispose();
            VerifyRecovery(File.Filename, Password, version);
        }

        internal static void VerifyRecovery(string filename, string password, int version)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using (var db = new LiteDatabase(new ConnectionString { Filename = filename, Password = password }))
                {
                    Verify(db, version);
                    db.Checkpoint();
                }
                AllocationReachability.Verify(filename, password);
            }
        }

        public void Dispose()
        {
            try { Engine.Dispose(); }
            finally { Database.Dispose(); File.Dispose(); }
        }
    }
}
