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
        [MappedTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Dispose_finishes_while_a_reader_is_paused_before_admission(string password)
        {
            var directory = Path.Combine(SharedMappedDirectory.Root, "litedb-pressure-dispose-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "test.db");
            var passed = false;
            try
            {
                using (var engine = new SharedEngine(new EngineSettings { Filename = file, Password = password })
                    { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
                using (var database = new LiteDatabase(engine, disposeOnClose: false))
                using (var ready = new ManualResetEventSlim())
                using (var resume = new ManualResetEventSlim())
                {
                    var rows = database.GetCollection("rows");
                    rows.InsertBulk(Enumerable.Range(0, 64).Select(id => new BsonDocument
                        { ["_id"] = id, ["value"] = 7, ["payload"] = new string('x', 3000) }));
                    rows.EnsureIndex("value");
                    for (var i = 0; i < 3; i++) rows.FindById(0)["value"].AsInt32.Should().Be(7);
                    engine.ForceCoordinatedYield = true;
                    engine.CoordinationStage = stage =>
                    {
                        if (stage != "writer-pressure") return;
                        engine.CoordinationStage = null;
                        ready.Set();
                        if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Paused reader before disposal");
                    };
                    var reading = Task.Run(() => Record.Exception(() => rows.FindById(63)));
                    Task disposing = null;
                    try
                    {
                        ready.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                        using (var registry = new LiteDB.Client.Shared.SharedReaderRegistry(file))
                            registry.LiveVersions().Should().BeEmpty();
                        disposing = Task.Run(() => engine.Dispose());
                        disposing.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the paused call owns neither a lease nor admission");
                        resume.Set();
                        reading.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                        reading.Result.Should().BeOfType<ObjectDisposedException>();
                    }
                    finally
                    {
                        resume.Set();
                        reading.Wait(TimeSpan.FromSeconds(10));
                        disposing?.Wait(TimeSpan.FromSeconds(10));
                    }
                    using (var registry = new LiteDB.Client.Shared.SharedReaderRegistry(file))
                        registry.LiveVersions().Should().BeEmpty("disposal must leave no lease behind");
                }
                using (var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = password }))
                {
                    var rows = cold.GetCollection("rows").Find(Query.EQ("value", 7)).OrderBy(row => row["_id"].AsInt32).ToArray();
                    rows.Select(row => row["_id"].AsInt32).Should().Equal(Enumerable.Range(0, 64));
                    rows.Should().OnlyContain(row => row["payload"].AsString == new string('x', 3000));
                }
                passed = true;
            }
            finally { if (passed) Directory.Delete(directory, true); }
        }

        [MappedTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Measured_streaming_reader_preserves_old_rows_and_releases_its_lease_on_another_thread(string password)
        {
            var directory = Path.Combine(SharedMappedDirectory.Root, "litedb-pressure-stream-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "test.db");
            var passed = false;
            BsonDocument Row(int id, int value) => new BsonDocument
                { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 3000) };
            try
            {
                using (var engine = new SharedEngine(new EngineSettings { Filename = file, Password = password })
                    { CoordinatedIdleLimit = TimeSpan.FromMinutes(1), ForceCoordinatedYield = true })
                using (var reader = new LiteDatabase(engine))
                using (var writer = new LiteDatabase(new ConnectionString
                    { Filename = file, Password = password, Connection = ConnectionType.Shared }))
                {
                    writer.CheckpointSize = 0;
                    var rows = writer.GetCollection("rows");
                    rows.InsertBulk(Enumerable.Range(0, 64).Select(id => Row(id, 0)));
                    rows.EnsureIndex("value");
                    for (var i = 0; i < 3; i++) reader.GetCollection("rows").FindById(0);
                    using (var held = reader.GetCollection("rows").Query().OrderBy("_id").ToEnumerable().GetEnumerator())
                    {
                        held.MoveNext().Should().BeTrue();
                        engine.MeasuredStreamingReaders.Should().Be(1);
                        rows.Update(Enumerable.Range(0, 64).Select(id => Row(id, 7)));
                        writer.Checkpoint();
                        reader.GetCollection("rows").FindById(63)["value"].AsInt32.Should().Be(7);
                        var count = 0;
                        do
                        {
                            held.Current["_id"].AsInt32.Should().Be(count++);
                            held.Current["value"].AsInt32.Should().Be(0);
                            held.Current["payload"].AsString.Should().Be(new string('x', 3000));
                        } while (count < 32 && held.MoveNext());
                        count.Should().Be(32, "the measured reader must outlive the buffered prefix");
                        Task.Run(() => held.Dispose()).Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    }
                    using var registry = new LiteDB.Client.Shared.SharedReaderRegistry(file);
                    registry.LiveVersions().Should().BeEmpty();
                    writer.Checkpoint();
                }
                using (var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = password }))
                {
                    var rows = cold.GetCollection("rows").Find(Query.EQ("value", 7)).OrderBy(row => row["_id"].AsInt32).ToArray();
                    rows.Select(row => row["_id"].AsInt32).Should().Equal(Enumerable.Range(0, 64));
                    rows.Should().OnlyContain(row => row["payload"].AsString == new string('x', 3000));
                }
                passed = true;
            }
            finally { if (passed) Directory.Delete(directory, true); }
        }

        [MappedTheory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void Scheduling_precedes_leases_but_an_admitted_reader_keeps_its_generation(string password, bool beforeAdmission)
        {
            var directory = Path.Combine(SharedMappedDirectory.Root, "litedb-pressure-" + Guid.NewGuid().ToString("N"));
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
                        if (stage != (beforeAdmission ? "writer-pressure" : "admitted")) return;
                        engine.CoordinationStage = null;
                        ready.Set();
                        if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Paused accepted reader");
                    };
                    var reading = Task.Run(() => rows.FindById(63));
                    try
                    {
                        ready.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                        if (beforeAdmission)
                        {
                            using var registry = new LiteDB.Client.Shared.SharedReaderRegistry(file);
                            registry.LiveVersions().Should().BeEmpty("scheduling must not retain a reader lease");
                        }
                        writer.GetCollection("rows").Update(Enumerable.Range(0, 64).Select(id => Row(id, 7))).Should().Be(64);
                        writer.Checkpoint();
                        resume.Set();
                        reading.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                        reading.Result["value"].AsInt32.Should().Be(beforeAdmission ? 7 : 0);
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
