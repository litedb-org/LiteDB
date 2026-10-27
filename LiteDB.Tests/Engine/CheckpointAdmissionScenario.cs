using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;

namespace LiteDB.Tests.Engine
{
    internal sealed class CheckpointAdmissionScenario : IDisposable
    {
        internal readonly TempFile File = new TempFile();
        internal readonly LiteEngine Engine;
        internal readonly LiteDatabase Database;
        internal readonly DiskService Disk;
        internal readonly TransactionMonitor Monitor;
        internal readonly EngineState State;
        internal WalIndexService Wal => Engine.GetWalIndex();

        internal CheckpointAdmissionScenario(int checkpoint = 1)
        {
            Engine = new LiteEngine(new EngineSettings { Filename = File.Filename, TransactionPageLimit = 8 });
            Database = new LiteDatabase(Engine, disposeOnClose: false);
            Disk = Field<DiskService>("_disk");
            Monitor = Field<TransactionMonitor>("_monitor");
            State = Field<EngineState>("_state");
            Database.CheckpointSize = 0;
            Database.GetCollection("rows").EnsureIndex("value");
            Database.GetCollection("rows").Insert(Row(0));
            Database.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "untouched" });
            Database.Timeout = TimeSpan.FromSeconds(1);
            Database.Checkpoint();
            Database.CheckpointSize = checkpoint;
            Database.Checkpoint();
            Disk.WriteAdmission = new CheckpointWriteAdmission(checkpoint, Disk.GetFileLength(FileOrigin.Log));
        }

        internal T Field<T>(string name) => (T)typeof(LiteEngine)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Engine);

        internal static BsonDocument Row(int id)
        {
            var bytes = new byte[75000];
            new Random(1000 + id).NextBytes(bytes);
            return new BsonDocument
            {
                ["_id"] = id, ["value"] = id * 7,
                ["payload"] = id == 0 ? new string('x', 1000) : Convert.ToBase64String(bytes)
            };
        }

        internal void VerifyRecovery(bool committed)
        {
            this.DisposeEngine();
            for (var i = 0; i < 3; i++)
            {
                using var db = new LiteDatabase(File.Filename);
                var rows = db.GetCollection("rows");
                var expected = committed ? new[] { Row(0), Row(1) } : new[] { Row(0) };
                rows.FindAll().OrderBy(x => x["_id"].AsInt32).Should().BeEquivalentTo(expected);
                foreach (var row in expected)
                    rows.Find(Query.EQ("value", row["value"])).Should().BeEquivalentTo(new[] { row });
                rows.Find(Query.EQ("value", 7)).Should().HaveCount(committed ? 1 : 0);
                db.GetCollection("sentinel").FindById(1)["payload"].AsString.Should().Be("untouched");
                db.Checkpoint();
            }
        }

        private void DisposeEngine()
        {
            Database.Dispose();
            Engine.Dispose();
        }

        public void Dispose()
        {
            try { this.DisposeEngine(); }
            finally { File.Dispose(); }
        }
    }
}
