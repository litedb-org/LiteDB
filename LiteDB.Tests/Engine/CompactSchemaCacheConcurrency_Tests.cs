using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Readers across more collections than the shared catalog cache retains,
    /// racing a writer that keeps admitting new shapes and checkpointing, must
    /// always decode every visible document to its exact written content.
    /// </summary>
    public class CompactSchemaCacheConcurrency_Tests
    {
        private static BsonDocument Doc(int collection, int shape, int id)
        {
            var doc = new BsonDocument { ["_id"] = id };
            for (var f = 0; f < 8; f++) doc[$"C{collection}Shape{shape}Field{f}"] = id * 100 + f;
            doc["NestedPayloadDocument"] = new BsonDocument { [$"Inner{shape % 5}Name"] = "v" + id, ["Shared"] = shape };
            return doc;
        }

        [Fact]
        public void Readers_never_observe_a_stale_or_foreign_catalog_under_cache_eviction_and_checkpoints()
        {
            using var file = new TempFile();
            const int collections = 40;
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, CompactStorage = CompactStorageMode.Auto });
            db.CheckpointSize = 0;
            for (var c = 0; c < collections; c++)
                db.GetCollection("col" + c).InsertBulk(Enumerable.Range(1, 4).Select(i => Doc(c, i / 2, i)));

            var expected = new ConcurrentDictionary<(int, int), BsonDocument>();
            for (var c = 0; c < collections; c++)
                foreach (var i in Enumerable.Range(1, 4)) expected[(c, i)] = Doc(c, i / 2, i);

            var failures = new ConcurrentQueue<string>();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var writer = Task.Run(() =>
            {
                var next = 5;
                while (!stop.IsCancellationRequested && failures.IsEmpty)
                {
                    var c = next % collections;
                    var shape = next / 2;
                    try
                    {
                        // Two single-document transactions admit the shape through the shared history.
                        foreach (var id in new[] { next, next + 1 })
                        {
                            var doc = Doc(c, shape, id);
                            db.GetCollection("col" + c).Insert(doc);
                            expected[(c, id)] = Doc(c, shape, id);
                        }
                        if (next % 10 == 5) db.Checkpoint();
                        if (next % 14 == 3)
                        {
                            db.BeginTrans();
                            db.GetCollection("col" + c).Insert(Doc(c, shape + 100000, -next));
                            db.GetCollection("col" + c).Insert(Doc(c, shape + 100000, -next - 1));
                            db.Rollback();
                        }
                    }
                    catch (Exception ex) { failures.Enqueue("writer: " + ex); }
                    next += 2;
                }
            });
            var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
            {
                var r = new Random(reader);
                while (!stop.IsCancellationRequested && failures.IsEmpty)
                {
                    var c = r.Next(collections);
                    try
                    {
                        foreach (var doc in db.GetCollection("col" + c).FindAll())
                        {
                            var id = doc["_id"].AsInt32;
                            if (!expected.TryGetValue((c, id), out var want))
                            {
                                failures.Enqueue($"reader {reader}: unexpected document col{c}/{id}: {doc}");
                                break;
                            }
                            if (!BsonSerializer.Serialize(doc).SequenceEqual(BsonSerializer.Serialize(want)))
                                failures.Enqueue($"reader {reader}: col{c}/{id} decoded as {doc} instead of {want}");
                        }
                    }
                    catch (Exception ex) { failures.Enqueue($"reader {reader}: col{c}: {ex}"); }
                }
            })).ToArray();

            Task.WaitAll(readers.Concat(new[] { writer }).ToArray());
            failures.Take(5).Should().BeEmpty();
            for (var c = 0; c < collections; c++)
            {
                var docs = db.GetCollection("col" + c).FindAll().ToList();
                docs.Select(d => d["_id"].AsInt32).Should().OnlyContain(id => id > 0, "rolled-back documents must not be visible");
                foreach (var doc in docs)
                    BsonSerializer.Serialize(doc).Should().Equal(BsonSerializer.Serialize(expected[(c, doc["_id"].AsInt32)]));
            }
            db.Execute("SELECT COUNT(*) FROM $dump WHERE $.pageType = 'Schema'").ToEnumerable().Single().AsDocument.Values.Single().AsInt32
                .Should().BeGreaterOrEqualTo(collections, "the race must exercise one compact catalog per collection");
        }
    }
}
