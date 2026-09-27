using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedWalReusePublication_Tests
    {
        [Fact]
        public void Safepoint_prefix_rewrites_publish_reuse_before_the_disk_write()
        {
            using (var file = new TempFile())
            {
                var signals = new Signals();
                var rewrites = 0;
                using (var engine = new LiteEngine(new EngineSettings
                { Filename = file, TransactionPageLimit = 1, CoordinationSignals = signals }))
                using (var db = new LiteDatabase(engine))
                {
                    var rows = db.GetCollection("rows");
                    for (var id = 0; id < 40; id++)
                        rows.Insert(new BsonDocument { ["_id"] = id, ["value"] = 0, ["payload"] = new string('x', 4000) });
                    var seen = new Dictionary<long, int>();
                    engine.SimulateDiskWriteFail = page =>
                    {
                        if (seen.TryGetValue(page.Position, out var prior))
                        {
                            signals.Reuses.Should().BeGreaterThan(prior, "reuse must precede each physical prefix overwrite");
                            rewrites++;
                        }
                        seen[page.Position] = signals.Reuses;
                    };
                    db.BeginTrans();
                    for (var revision = 1; revision <= 4; revision++)
                        for (var id = 0; id < 40; id++)
                            rows.Update(new BsonDocument { ["_id"] = id, ["value"] = revision, ["payload"] = new string('x', 4000) });
                    db.Commit();
                    engine.SimulateDiskWriteFail = null;
                }
                rewrites.Should().BeGreaterThan(0, "the test must reach in-transaction WAL slot reuse");
                using (var reopened = new LiteDatabase(file.Filename))
                {
                    var rows = reopened.GetCollection("rows");
                    rows.Count().Should().Be(40);
                    for (var id = 0; id < 40; id++)
                    {
                        var row = rows.FindById(id);
                        row["value"].AsInt32.Should().Be(4);
                        row["payload"].AsString.Should().Be(new string('x', 4000));
                    }
                }
            }
        }

        private sealed class Signals : ICoordinationSignals
        {
            internal int Reuses;
            public void StructuralBegin() { }
            public void StructuralEnd(int version) { }
            public void SlotReused() { Reuses++; }
            public void Committed(int version) { }
        }
    }
}
