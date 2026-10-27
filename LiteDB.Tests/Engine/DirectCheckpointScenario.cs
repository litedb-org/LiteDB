using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    internal sealed class DirectCheckpointScenario : IDisposable
    {
        internal readonly TempFile File = new TempFile();
        internal readonly LiteEngine Engine;
        internal readonly LiteDatabase Database;
        internal readonly DiskService Disk;
        internal readonly EngineState State;
        internal readonly AutomaticCheckpointCoordinator Worker;

        internal DirectCheckpointScenario(int checkpoint = 1000)
        {
            Engine = new LiteEngine(File.Filename);
            Database = new LiteDatabase(Engine, disposeOnClose: false);
            Database.CheckpointSize = 0;
            Database.GetCollection("rows").EnsureIndex("value");
            Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(0));
            Database.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "untouched" });
            Database.Checkpoint();
            Database.CheckpointSize = checkpoint;
            Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(1));
            Disk = Field<DiskService>("_disk");
            State = Field<EngineState>("_state");
            Worker = Engine.InstallDirectCheckpointLifetime();
        }

        internal AutomaticCheckpointCoordinator CurrentWorker => Field<DiskService>("_disk").AutomaticCheckpoint;

        private T Field<T>(string name) => (T)typeof(LiteEngine)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Engine);

        internal void VerifyRecovery(bool uncertain = false, int? userVersion = null)
        {
            Record.Exception(Engine.Dispose);
            for (var open = 0; open < 3; open++)
            {
                using var db = new LiteDatabase(File.Filename);
                if (userVersion.HasValue) db.UserVersion.Should().Be(userVersion.Value);
                var rows = db.GetCollection("rows");
                var documents = rows.FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
                documents.Select(row => row["_id"].AsInt32).Should().Contain(new[] { 0, 1 });
                documents.Should().OnlyContain(row => row["_id"].AsInt32 <= (uncertain ? 2 : 1));
                foreach (var row in documents) ((object)row).Should().BeEquivalentTo(BackgroundCheckpointScenario.Row(row["_id"].AsInt32));
                foreach (var id in new[] { 0, 1, 2 })
                    rows.Find(Query.EQ("value", id * 7)).Should().BeEquivalentTo(documents.Where(row => row["_id"].AsInt32 == id));
                db.GetCollection("sentinel").FindById(1)["payload"].AsString.Should().Be("untouched");
                db.Checkpoint();
            }
        }

        public void Dispose()
        {
            Record.Exception(Engine.Dispose);
            Database.Dispose();
            File.Dispose();
        }
    }
}
