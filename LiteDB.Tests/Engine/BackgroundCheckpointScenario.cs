using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;

namespace LiteDB.Tests.Engine
{
    // Shared file-backed oracle for staged worker/admission tests. Activation is
    // explicit and happens before concurrency; production never installs it yet.
    internal sealed class BackgroundCheckpointScenario : IDisposable
    {
        internal readonly TempFile File = new TempFile();
        internal readonly LiteEngine Engine;
        internal readonly LiteDatabase Database;
        internal readonly DiskService Disk;
        internal readonly EngineState State;
        internal WalIndexService Wal => Engine.GetWalIndex();

        internal BackgroundCheckpointScenario(bool barrier = true)
        {
            Engine = new LiteEngine(new EngineSettings { Filename = File.Filename });
            Database = new LiteDatabase(Engine, disposeOnClose: false);
            Disk = Field<DiskService>("_disk");
            State = Field<EngineState>("_state");
            Database.CheckpointSize = 0;
            Database.GetCollection("rows").EnsureIndex("value");
            Database.GetCollection("rows").Insert(Row(0));
            Database.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "untouched" });
            Database.Checkpoint();
            // Leave an acknowledged predecessor in WAL so a checkpoint under a
            // pending group has actual safe backfill work, not just a no-op.
            Database.UserVersion = 17;
            if (barrier) Disk.CheckpointBarrier = new CheckpointBarrier();
        }

        internal static BsonDocument Row(int id) => new BsonDocument
        {
            ["_id"] = id, ["value"] = id * 7, ["payload"] = new string((char)('a' + id), 1000)
        };

        internal void VerifyRecovery(bool acknowledged, int userVersion = 17)
        {
            Database.Dispose();
            Engine.Dispose();
            for (var open = 0; open < 3; open++)
            {
                using var db = new LiteDatabase(File.Filename);
                db.UserVersion.Should().Be(userVersion);
                var rows = db.GetCollection("rows");
                var documents = rows.FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
                documents.Select(row => row["_id"].AsInt32).Should().Contain(0).And.OnlyContain(id => id == 0 || id == 1);
                if (acknowledged) documents.Select(row => row["_id"].AsInt32).Should().Equal(0, 1);
                foreach (var row in documents) ((object)row).Should().BeEquivalentTo(Row(row["_id"].AsInt32));
                foreach (var id in new[] { 0, 1 })
                    rows.Find(Query.EQ("value", id * 7)).Should()
                        .BeEquivalentTo(documents.Where(row => row["_id"].AsInt32 == id));
                db.GetCollection("sentinel").FindById(1)["payload"].AsString.Should().Be("untouched");
                db.Checkpoint();
            }
        }

        private T Field<T>(string name) => (T)typeof(LiteEngine)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Engine);

        public void Dispose()
        {
            try { Database.Dispose(); }
            finally
            {
                try { Engine.Dispose(); }
                finally { File.Dispose(); }
            }
        }
    }
}
