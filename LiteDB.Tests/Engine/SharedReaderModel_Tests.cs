using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Several SharedEngine instances in one process, writers on separate threads, streamed
    /// readers held across many partial checkpoints, compared against an independent model.
    /// </summary>
    public class SharedReaderModel_Tests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-shared-model-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public SharedReaderModel_Tests(ITestOutputHelper output)
        {
            _output = output;
            Directory.CreateDirectory(_directory);
        }

        private static BsonDocument Doc(int id, int value, int size) => new BsonDocument
        {
            ["_id"] = id, ["value"] = value, ["payload"] = new string((char)('a' + value % 26), size)
        };

        private sealed class Held
        {
            internal IBsonDataReader Reader;
            internal Dictionary<int, BsonDocument> Expected;
            internal HashSet<int> Seen = new HashSet<int>();
            internal int CloseAt;
            internal bool Done;
            internal string Collection;
        }

        [Theory]
        [InlineData(11, null)]
        [InlineData(12, "secret")]
        public void InterleavedSharedInstances_ReadersMatchModel(int seed, string password)
        {
            var random = new Random(seed);
            var names = new[] { "a", "b" };
            var model = names.ToDictionary(n => n, _ => new Dictionary<int, BsonDocument>());
            Func<SharedEngine> open = () => new SharedEngine(new EngineSettings { Filename = Filename, Password = password });
            var engines = new[] { open(), open(), open() };
            var held = new List<Held>();
            var generation = 1;
            try
            {
                foreach (var name in names)
                {
                    var docs = Enumerable.Range(1, 160).Select(id => Doc(id, 0, 1400)).ToArray();
                    engines[0].Insert(name, docs, BsonAutoId.Int32);
                    engines[0].EnsureIndex(name, "value", BsonExpression.Create("$.value"), false);
                    foreach (var d in docs) model[name][d["_id"].AsInt32] = d;
                }

                for (var step = 0; step < 40; step++)
                {
                    if (random.Next(3) == 0 || held.Count == 0)
                    {
                        var name = names[random.Next(names.Length)];
                        var engine = engines[random.Next(engines.Length)];
                        held.Add(new Held
                        {
                            Collection = name,
                            Expected = new Dictionary<int, BsonDocument>(model[name]),
                            Reader = engine.Query(name, new Query()),
                            CloseAt = step + random.Next(2, 12)
                        });
                    }

                    var writerName = names[random.Next(names.Length)];
                    var writer = engines[random.Next(engines.Length)];
                    var value = generation++;
                    var kind = random.Next(4);
                    MvccCheckpoint_Tests.RunThread(() =>
                    {
                        if (kind == 0)
                        {
                            var docs = model[writerName].Keys.Select(id => Doc(id, value, 1000 + id % 5 * 200)).ToArray();
                            writer.Update(writerName, docs);
                            foreach (var d in docs) model[writerName][d["_id"].AsInt32] = d;
                        }
                        else if (kind == 1)
                        {
                            var ids = model[writerName].Keys.Take(3).ToArray();
                            writer.Delete(writerName, ids.Select(x => new BsonValue(x)));
                            foreach (var id in ids) model[writerName].Remove(id);
                            var d = Doc(10000 + value, value, 2500);
                            writer.Insert(writerName, new[] { d }, BsonAutoId.Int32);
                            model[writerName][10000 + value] = d;
                        }
                        else if (kind == 2)
                        {
                            writer.BeginTrans();
                            writer.Update(writerName, model[writerName].Keys.Select(id => Doc(id, -value, 3000)));
                            writer.Rollback();
                        }
                        else writer.Checkpoint();
                    });

                    foreach (var h in held) Advance(h, random.Next(0, 30));
                    foreach (var h in held.Where(x => x.CloseAt <= step).ToArray())
                    {
                        Advance(h, int.MaxValue);
                        h.Reader.Dispose();
                        held.Remove(h);
                    }

                    // A fresh reader from a different instance sees exactly the model.
                    foreach (var name in names)
                    {
                        using var fresh = engines[random.Next(engines.Length)].Query(name, new Query());
                        var seen = new Dictionary<int, BsonDocument>();
                        while (fresh.Read()) seen.Add(fresh.Current["_id"].AsInt32, fresh.Current.AsDocument);
                        seen.Keys.Should().BeEquivalentTo(model[name].Keys, $"step {step} fresh {name}");
                        foreach (var kv in seen)
                            if (kv.Value != model[name][kv.Key]) throw new Exception($"step {step} fresh {name} id {kv.Key}: expected {model[name][kv.Key]["value"]} got {kv.Value["value"]}");
                    }
                }

                foreach (var h in held)
                {
                    Advance(h, int.MaxValue);
                    h.Reader.Dispose();
                }
                held.Clear();
            }
            finally
            {
                foreach (var h in held) h.Reader.Dispose();
                foreach (var e in engines) e.Dispose();
            }

            // Reopen (direct), compare, checkpoint, reopen again.
            for (var pass = 0; pass < 2; pass++)
            {
                using var engine = new LiteEngine(new EngineSettings { Filename = Filename, Password = password });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                foreach (var name in names)
                {
                    var col = db.GetCollection(name);
                    var all = col.FindAll().ToDictionary(x => x["_id"].AsInt32);
                    all.Keys.Should().BeEquivalentTo(model[name].Keys);
                    foreach (var kv in all) (kv.Value == model[name][kv.Key]).Should().BeTrue($"pass {pass} {name} {kv.Key}");
                    foreach (var group in model[name].Values.GroupBy(x => x["value"].AsInt32))
                        col.Count(Query.EQ("value", group.Key)).Should().Be(group.Count());
                    col.Count(Query.LT("value", 0)).Should().Be(0);
                }
                db.Checkpoint();
            }
        }

        private static void Advance(Held h, int count)
        {
            for (var i = 0; i < count && !h.Done; i++)
            {
                if (!h.Reader.Read())
                {
                    h.Done = true;
                    h.Seen.Should().BeEquivalentTo(h.Expected.Keys, $"held reader on {h.Collection}");
                    return;
                }
                var id = h.Reader.Current["_id"].AsInt32;
                h.Expected.Should().ContainKey(id);
                h.Seen.Add(id).Should().BeTrue();
                if (h.Reader.Current.AsDocument != h.Expected[id])
                    throw new Exception($"held reader on {h.Collection} id {id}: expected {h.Expected[id]["value"]} got {h.Reader.Current["value"]}");
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
