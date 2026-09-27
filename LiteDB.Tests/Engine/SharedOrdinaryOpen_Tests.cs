#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedOrdinaryOpen_Tests
    {
        [MappedTheory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void Warm_reader_runs_during_real_writable_open_and_observes_commit_or_rollback(string password, bool rollback)
        {
            var directory = Path.Combine(SharedMappedDirectory.Root, "litedb-ordinary-open-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "test.db");
            var success = false;
            try
            {
                using (var writer = new LiteDatabase(new ConnectionString
                    { Filename = file, Password = password, Connection = ConnectionType.Shared }))
                using (var engine = new SharedEngine(new EngineSettings { Filename = file, Password = password })
                    { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
                using (var reader = new LiteDatabase(engine))
                using (var finished = new ManualResetEventSlim())
                {
                    writer.CheckpointSize = 0;
                    var rows = writer.GetCollection("rows");
                    rows.InsertBulk(Enumerable.Range(0, 64).Select(id => Row(id, 0)));
                    rows.EnsureIndex("generation");
                    for (var i = 0; i < 3; i++) reader.GetCollection("rows").FindById(0);
                    engine.MutexOwner.WaitForRelease();
                    var hits = engine.CoordinatedReadHits;
                    Exception error = null;
                    var worker = new Thread(() =>
                    {
                        try
                        {
                            var row = reader.GetCollection("rows").FindById(63);
                            row["generation"].AsInt32.Should().Be(0);
                            row["payload"].AsString.Should().Be(new string('a', 1000));
                        }
                        catch (Exception exception) { error = exception; }
                        finally { finished.Set(); }
                    }) { IsBackground = true };
                    writer.BeginTrans().Should().BeTrue();
                    var completedUnderWriter = false;
                    try
                    {
                        rows.Update(Enumerable.Range(0, 64).Select(id => Row(id, 7)));
                        worker.Start();
                        completedUnderWriter = finished.Wait(TimeSpan.FromSeconds(5));
                    }
                    finally
                    {
                        if (rollback) writer.Rollback();
                        else writer.Commit();
                    }
                    worker.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    completedUnderWriter.Should().BeTrue("append-only writers must not invalidate the committed cached snapshot on open");
                    error.Should().BeNull();
                    engine.CoordinatedReadHits.Should().BeGreaterThan(hits);
                    writer.Checkpoint();
                    Validate(reader, rollback ? 0 : 7);
                }
                using (var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = password }))
                    Validate(cold, rollback ? 0 : 7);
                success = true;
            }
            finally { if (success) Directory.Delete(directory, true); }
        }

        private static BsonDocument Row(int id, int generation) => new BsonDocument
        {
            ["_id"] = id, ["generation"] = generation, ["payload"] = new string((char)('a' + generation), 1000)
        };

        private static void Validate(LiteDatabase database, int generation)
        {
            var rows = database.GetCollection("rows");
            rows.FindAll().OrderBy(row => row["_id"].AsInt32).Select(row => row["_id"].AsInt32)
                .Should().Equal(Enumerable.Range(0, 64));
            var indexed = rows.Find(Query.EQ("generation", generation)).ToArray();
            indexed.Should().HaveCount(64);
            foreach (var row in indexed) row["payload"].AsString.Should().Be(new string((char)('a' + generation), 1000));
        }
    }
}
#endif
