#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedWriterPressure_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void A_yield_after_admission_keeps_its_generation_through_commit_and_checkpoint(string password)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-pressure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "test.db");
            var passed = false;
            BsonDocument Row(int id, int value) => new BsonDocument
                { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 3000) };
            try
            {
                using (var engine = new SharedEngine(new EngineSettings { Filename = file, Password = password })
                    { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
                using (var database = new LiteDatabase(engine, disposeOnClose: false))
                using (var writer = new LiteDatabase(new ConnectionString
                    { Filename = file, Password = password, Connection = ConnectionType.Shared }))
                using (var ready = new ManualResetEventSlim())
                using (var resume = new ManualResetEventSlim())
                {
                    var rows = database.GetCollection("rows");
                    rows.InsertBulk(Enumerable.Range(0, 64).Select(id => Row(id, 0)));
                    rows.EnsureIndex("value");
                    for (var i = 0; i < 3; i++) rows.FindById(0)["value"].AsInt32.Should().Be(0);
                    engine.ForceCoordinatedYield = true;
                    engine.CoordinationStage = stage =>
                    {
                        if (stage != "writer-pressure") return;
                        ready.Set();
                        if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Paused accepted reader");
                    };
                    var reading = Task.Run(() => rows.FindById(63));
                    try
                    {
                        ready.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                        writer.GetCollection("rows").Update(Enumerable.Range(0, 64).Select(id => Row(id, 7))).Should().Be(64);
                        writer.Checkpoint();
                        resume.Set();
                        reading.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                        reading.Result["value"].AsInt32.Should().Be(0);
                        reading.Result["payload"].AsString.Should().Be(new string('x', 3000));
                        rows.FindById(63)["value"].AsInt32.Should().Be(7);
                    }
                    finally { resume.Set(); reading.Wait(TimeSpan.FromSeconds(10)); }
                }
                using (var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = password }))
                {
                    var rows = cold.GetCollection("rows").Find(Query.EQ("value", 7)).OrderBy(row => row["_id"].AsInt32).ToArray();
                    rows.Length.Should().Be(64);
                    for (var id = 0; id < rows.Length; id++)
                    {
                        rows[id]["_id"].AsInt32.Should().Be(id);
                        rows[id]["payload"].AsString.Should().Be(new string('x', 3000));
                    }
                }
                passed = true;
            }
            finally
            {
                if (passed) Directory.Delete(directory, true);
                else Console.Error.WriteLine("Preserved writer-pressure database: " + directory);
            }
        }
    }
}
#endif
