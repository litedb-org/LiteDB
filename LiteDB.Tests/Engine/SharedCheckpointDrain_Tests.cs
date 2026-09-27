using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedCheckpointDrain_Tests
    {
        [Theory]
        [InlineData(null, "departed")]
        [InlineData(null, "held")]
        [InlineData(null, "old")]
        [InlineData(null, "unknown")]
        [InlineData("secret", "departed")]
        [InlineData("secret", "held")]
        [InlineData("secret", "old")]
        [InlineData("secret", "unknown")]
        public void Bounded_drain_preserves_actual_readers_skips_old_generations_and_fails_closed_on_unknown(string password, string mode)
        {
            using var file = new TempFile();
            var signals = new Signals();
            var scan = 0;
            var inspecting = false;
            var version = 0;
            LiteDatabase held = null;
            BsonDocument Row(int id, int value) => new BsonDocument
                { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 4000) };
            var settings = new EngineSettings
            {
                Filename = file.Filename, Password = password, CoordinationSignals = signals,
                SharedReaderVersions = () =>
                {
                    if (!inspecting) return Array.Empty<int>();
                    signals.Depth.Should().BeGreaterThan(0, "both scans must remain inside the structural region");
                    scan++;
                    if (scan == 1 && mode == "departed") { held.Dispose(); held = null; }
                    if (scan == 2 && mode == "unknown") return null;
                    return held != null || scan == 1 ? new[] { version } : Array.Empty<int>();
                }
            };
            try
            {
                using (var engine = new LiteEngine(settings))
                using (var writer = new LiteDatabase(engine, disposeOnClose: false))
                {
                    writer.CheckpointSize = 0;
                    var rows = writer.GetCollection("rows");
                    rows.InsertBulk(Enumerable.Range(0, 64).Select(id => Row(id, 0)));
                    rows.EnsureIndex("value");
                    writer.Checkpoint();
                    var emptyLogLength = new FileInfo(FileHelper.GetLogFile(file.Filename)).Length;
                    version = engine.ReadVersion;
                    held = new LiteDatabase(new LiteEngine(new EngineSettings
                        { Filename = file.Filename, Password = password, ReadOnly = true, SharedReadSnapshot = true }));
                    held.GetCollection("rows").FindById(0)["value"].AsInt32.Should().Be(0);
                    if (mode == "old") rows.Update(Enumerable.Range(0, 64).Select(id => Row(id, 3)));
                    rows.Update(Enumerable.Range(0, 64).Select(id => Row(id, 7)));
                    var data = File.ReadAllBytes(file.Filename);
                    var log = File.ReadAllBytes(FileHelper.GetLogFile(file.Filename));
                    inspecting = true;
                    writer.Checkpoint();
                    if (mode == "old") scan.Should().Be(1, "a retained older generation must bypass the drain");
                    else if (mode == "held") scan.Should().BeInRange(1, 5, "the drain must remain bounded even if a reader stays active");
                    else scan.Should().Be(2);
                    signals.Depth.Should().Be(0);
                    if (mode == "departed") new FileInfo(FileHelper.GetLogFile(file.Filename)).Length.Should().Be(emptyLogLength);
                    else
                    {
                        held.GetCollection("rows").Find(Query.EQ("value", 0)).Count().Should().Be(64);
                        held.GetCollection("rows").FindAll().Should().OnlyContain(row =>
                            row["value"].AsInt32 == 0 && row["payload"].AsString == new string('x', 4000));
                    }
                    if (mode == "unknown")
                    {
                        File.ReadAllBytes(file.Filename).Should().Equal(data);
                        File.ReadAllBytes(FileHelper.GetLogFile(file.Filename)).Should().Equal(log);
                    }
                    held?.Dispose(); held = null; inspecting = false;
                    writer.Checkpoint();
                }
                using var cold = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password });
                var actual = cold.GetCollection("rows").Find(Query.EQ("value", 7)).OrderBy(row => row["_id"].AsInt32).ToArray();
                actual.Select(row => row["_id"].AsInt32).Should().Equal(Enumerable.Range(0, 64));
                actual.Should().OnlyContain(row => row["payload"].AsString == new string('x', 4000));
            }
            finally { held?.Dispose(); }
        }

        private sealed class Signals : IBatchedCoordinationSignals
        {
            internal int Depth;
            public void StructuralBegin() => Depth++;
            public void StructuralEnd(int version) => Depth--;
            public void SlotReused() { }
            public void Committed(int version) { }
        }
    }
}
