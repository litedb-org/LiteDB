using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2043_Tests
    {
        [Fact]
        public void Concurrent_Int64_auto_ids_match_document_ids_and_continue_after_rebuild_and_reopen()
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-2043-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "data.db");
            var ledger = new ConcurrentDictionary<long, string>();
            try
            {
                using (var db = new LiteDatabase(path))
                {
                    var col = db.GetCollection("records", BsonAutoId.Int64);
                    col.Insert(new BsonDocument { ["_id"] = 100L, ["value"] = "seed" });
                    ledger[100] = "seed";
                    Parallel.For(0, 4, worker =>
                    {
                        for (var round = 0; round < 100; round++)
                        {
                            var value = worker + "/" + round;
                            var id = col.Insert(new BsonDocument { ["value"] = value });
                            id.IsInt64.Should().BeTrue();
                            ledger.TryAdd(id.AsInt64, value).Should().BeTrue();
                            col.FindById(id)["value"].AsString.Should().Be(value);
                        }
                    });
                    db.Rebuild();
                }
                using var reopened = new LiteDatabase(path);
                var records = reopened.GetCollection("records", BsonAutoId.Int64);
                records.FindAll().ToDictionary(x => x["_id"].AsInt64, x => x["value"].AsString).Should().BeEquivalentTo(ledger);
                var next = records.Insert(new BsonDocument { ["value"] = "after reopen" });
                next.AsInt64.Should().BeGreaterThan(ledger.Keys.Max());
                records.FindById(next)["value"].AsString.Should().Be("after reopen");
                foreach (var pair in ledger) records.FindById(pair.Key)["value"].AsString.Should().Be(pair.Value);
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
