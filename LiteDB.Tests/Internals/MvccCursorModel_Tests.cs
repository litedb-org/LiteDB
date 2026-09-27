using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Internals
{
    /// <summary>
    /// Model check: long-lived cursors held across many partial checkpoints,
    /// several collections, rollbacks, safepoint-heavy transactions and reopen after every cycle.
    /// </summary>
    public class MvccCursorModel_Tests
    {
        private readonly ITestOutputHelper _output;
        public MvccCursorModel_Tests(ITestOutputHelper output) => _output = output;

        private static readonly string[] Collections = { "a", "b", "c" };

        private static BsonDocument Doc(int id, int value, int size) => new BsonDocument
        {
            ["_id"] = id, ["value"] = value, ["payload"] = new string((char)('a' + value % 26), size),
            ["nested"] = new BsonDocument { ["id"] = id, ["gen"] = value }
        };

        private sealed class Model
        {
            internal readonly Dictionary<string, Dictionary<int, BsonDocument>> Data =
                Collections.ToDictionary(c => c, _ => new Dictionary<int, BsonDocument>());

            internal Dictionary<int, BsonDocument> Snapshot(string collection) =>
                new Dictionary<int, BsonDocument>(Data[collection]);
        }

        private sealed class OpenCursor
        {
            internal string Collection;
            internal IBsonDataReader Reader;
            internal Dictionary<int, BsonDocument> Expected;
            internal HashSet<int> Seen = new HashSet<int>();
            internal int CloseAtCycle;
            internal bool Finished;
        }

        [Theory]
        [InlineData(1, null, CompactStorageMode.Legacy)]
        [InlineData(2, "secret", CompactStorageMode.Legacy)]
        [InlineData(3, null, CompactStorageMode.Auto)]
        [InlineData(4, null, CompactStorageMode.Legacy)]
        public void LongLivedCursorsAcrossCheckpoints_MatchModel(int seed, string password, CompactStorageMode compact)
        {
            var random = new Random(seed);
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password, TransactionPageLimit = 2, CompactStorage = compact };
            var engine = new LiteEngine(settings);
            var db = new LiteDatabase(engine, disposeOnClose: false);
            db.Pragma(Pragmas.CHECKPOINT, 0);
            var model = new Model();
            foreach (var name in Collections)
            {
                db.GetCollection(name).EnsureIndex("value");
                var docs = Enumerable.Range(1, 120).Select(id => Doc(id, 0, 900 + id % 7 * 100)).ToArray();
                db.GetCollection(name).Insert(docs);
                foreach (var doc in docs) model.Data[name][doc["_id"].AsInt32] = doc;
            }

            var cursors = new List<OpenCursor>();
            var generation = 1;
            try
            {
                for (var cycle = 0; cycle < 18; cycle++)
                {
                    // Open one or two cursors held across several cycles.
                    var opens = random.Next(0, 3);
                    for (var i = 0; i < opens; i++)
                    {
                        var name = Collections[random.Next(Collections.Length)];
                        var cursor = new OpenCursor
                        {
                            Collection = name,
                            Expected = model.Snapshot(name),
                            Reader = engine.Query(name, new Query()),
                            CloseAtCycle = cycle + random.Next(1, 6)
                        };
                        cursors.Add(cursor);
                    }
                    foreach (var cursor in cursors) Advance(cursor, random.Next(0, 25));

                    var ops = random.Next(2, 6);
                    for (var op = 0; op < ops; op++)
                    {
                        var kind = random.Next(0, 8);
                        var name = Collections[random.Next(Collections.Length)];
                        var value = generation++;
                        MvccCheckpoint_Tests.RunThread(() =>
                        {
                            var col = db.GetCollection(name);
                            switch (kind)
                            {
                                case 0: // update all documents (many pages, many obsolete frames)
                                case 1:
                                {
                                    var ids = model.Data[name].Keys.ToArray();
                                    var docs = ids.Select(id => Doc(id, value, 800 + (id + value) % 9 * 150)).ToArray();
                                    col.Update(docs);
                                    foreach (var d in docs) model.Data[name][d["_id"].AsInt32] = d;
                                    break;
                                }
                                case 2: // update a few documents
                                {
                                    var ids = model.Data[name].Keys.OrderBy(_ => random.Next()).Take(5).ToArray();
                                    foreach (var id in ids)
                                    {
                                        var d = Doc(id, value, 1200);
                                        col.Update(d);
                                        model.Data[name][id] = d;
                                    }
                                    break;
                                }
                                case 3: // delete a few and insert new ones
                                {
                                    var ids = model.Data[name].Keys.OrderBy(_ => random.Next()).Take(4).ToArray();
                                    foreach (var id in ids)
                                    {
                                        col.Delete(id);
                                        model.Data[name].Remove(id);
                                    }
                                    var next = model.Data[name].Keys.DefaultIfEmpty(0).Max() + 1000 + value;
                                    for (var k = 0; k < 4; k++)
                                    {
                                        var d = Doc(next + k, value, 1500);
                                        col.Insert(d);
                                        model.Data[name][next + k] = d;
                                    }
                                    break;
                                }
                                case 4: // explicit transaction with safepoints, rolled back
                                {
                                    db.BeginTrans().Should().BeTrue();
                                    var ids = model.Data[name].Keys.ToArray();
                                    col.Update(ids.Select(id => Doc(id, -value, 2000)));
                                    col.Insert(Doc(900000 + value, -value, 3000));
                                    db.Rollback().Should().BeTrue();
                                    break;
                                }
                                case 5: // explicit multi-collection transaction committed
                                {
                                    db.BeginTrans().Should().BeTrue();
                                    foreach (var other in Collections)
                                    {
                                        var ids = model.Data[other].Keys.Take(40).ToArray();
                                        var docs = ids.Select(id => Doc(id, value, 1000)).ToArray();
                                        db.GetCollection(other).Update(docs);
                                        foreach (var d in docs) model.Data[other][d["_id"].AsInt32] = d;
                                    }
                                    db.Commit().Should().BeTrue();
                                    break;
                                }
                                default:
                                    engine.Checkpoint();
                                    break;
                            }
                        });
                        foreach (var cursor in cursors) Advance(cursor, random.Next(0, 10));
                    }

                    MvccCheckpoint_Tests.RunThread(() => engine.Checkpoint());

                    // Finish cursors due this cycle.
                    foreach (var cursor in cursors.Where(c => c.CloseAtCycle <= cycle).ToArray())
                    {
                        Advance(cursor, int.MaxValue);
                        cursor.Reader.Dispose();
                        cursors.Remove(cursor);
                    }

                    // Reopen copies (writable then read-only) and compare with model.
                    VerifyClone(data, log, password, compact, model, $"cycle {cycle}");
                    _output.WriteLine($"cycle {cycle}: cursors={cursors.Count} log={log.Length}");
                }

                foreach (var cursor in cursors)
                {
                    Advance(cursor, int.MaxValue);
                    cursor.Reader.Dispose();
                }
                cursors.Clear();
                engine.Checkpoint();
                log.Length.Should().Be(password == null ? 0 : Constants.PAGE_SIZE, "no reader remains after all cursors closed");
                VerifyClone(data, log, password, compact, model, "final");
            }
            finally
            {
                foreach (var cursor in cursors) cursor.Reader.Dispose();
                db.Dispose();
                engine.Dispose();
            }
        }

        private static void Advance(OpenCursor cursor, int count)
        {
            for (var i = 0; i < count && !cursor.Finished; i++)
            {
                if (!cursor.Reader.Read())
                {
                    cursor.Finished = true;
                    cursor.Seen.Should().BeEquivalentTo(cursor.Expected.Keys, $"cursor on {cursor.Collection} must see its snapshot's ids");
                    return;
                }
                var doc = cursor.Reader.Current.AsDocument;
                var id = doc["_id"].AsInt32;
                cursor.Expected.Should().ContainKey(id, $"cursor on {cursor.Collection} saw an id outside its snapshot");
                cursor.Seen.Add(id).Should().BeTrue("no duplicates");
                if (doc != cursor.Expected[id])
                {
                    throw new Exception($"cursor on {cursor.Collection} id {id}: expected value {cursor.Expected[id]["value"]} got {doc["value"]}");
                }
            }
        }

        private static void VerifyClone(MemoryStream data, MemoryStream log, string password, CompactStorageMode compact,
            Model model, string label)
        {
            var dataBytes = data.ToArray();
            var logBytes = log.ToArray();
            foreach (var readOnly in new[] { true, false })
            {
                using var d = new MemoryStream();
                using var l = new MemoryStream();
                d.Write(dataBytes, 0, dataBytes.Length);
                l.Write(logBytes, 0, logBytes.Length);
                d.Position = l.Position = 0;
                using (var engine = new LiteEngine(new EngineSettings { DataStream = d, LogStream = l, Password = password, ReadOnly = readOnly, CompactStorage = compact }))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    foreach (var name in Collections) Compare(db, name, model, $"{label} readOnly={readOnly}");
                    if (!readOnly) db.Checkpoint();
                }
                if (readOnly)
                {
                    d.ToArray().SequenceEqual(dataBytes).Should().BeTrue($"{label}: read-only open changed data bytes");
                    l.ToArray().SequenceEqual(logBytes).Should().BeTrue($"{label}: read-only open changed log bytes");
                }
                else
                {
                    // Second open of the fully checkpointed copy.
                    using var engine = new LiteEngine(new EngineSettings { DataStream = d, LogStream = l, Password = password, CompactStorage = compact });
                    using var db = new LiteDatabase(engine, disposeOnClose: false);
                    foreach (var name in Collections) Compare(db, name, model, $"{label} after checkpoint");
                }
            }
        }

        private static void Compare(LiteDatabase db, string name, Model model, string label)
        {
            var expected = model.Data[name];
            var col = db.GetCollection(name);
            var actual = col.FindAll().ToArray();
            actual.Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(expected.Keys, $"{label}: {name} ids");
            foreach (var doc in actual)
            {
                if (doc != expected[doc["_id"].AsInt32])
                    throw new Exception($"{label}: {name} id {doc["_id"]} expected value {expected[doc["_id"].AsInt32]["value"]} got {doc["value"]}");
            }
            // Index oracle: every present value is findable via the secondary index, stale ones are absent.
            foreach (var group in expected.Values.GroupBy(x => x["value"].AsInt32))
            {
                col.Count(Query.EQ("value", group.Key)).Should().Be(group.Count(), $"{label}: {name} index value {group.Key}");
            }
            col.Count(Query.LT("value", 0)).Should().Be(0, $"{label}: {name} rolled-back keys visible");
        }
    }
}
