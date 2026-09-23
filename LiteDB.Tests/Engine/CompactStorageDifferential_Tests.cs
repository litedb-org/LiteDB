using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Adversarial differential checks: identical operations against a Legacy (BSON)
    /// database and against Compact/Auto databases must produce identical documents,
    /// query results, projections and index results.
    /// </summary>
    public partial class CompactStorageDifferential_Tests
    {
        private readonly ITestOutputHelper _output;

        public CompactStorageDifferential_Tests(ITestOutputHelper output) => _output = output;

        internal sealed class Store : IDisposable
        {
            private readonly TempFile _file = new TempFile();
            private readonly int? _transactionPageLimit;
            public CompactStorageMode Mode { get; }
            public LiteDatabase Db { get; private set; }

            public Store(CompactStorageMode mode, int? transactionPageLimit = null)
            {
                Mode = mode;
                _transactionPageLimit = transactionPageLimit;
                Open();
            }

            public string Filename => _file.Filename;

            public void Open()
            {
                var connection = new ConnectionString { Filename = _file.Filename, CompactStorage = Mode };
                if (_transactionPageLimit.HasValue) connection.TransactionPageLimit = _transactionPageLimit.Value;
                Db = new LiteDatabase(connection);
            }

            public void Reopen()
            {
                Db.Dispose();
                Open();
            }

            public int SchemaPages()
            {
                using var reader = Db.Execute("SELECT $ FROM $dump WHERE $.pageType = 'Schema'");
                var count = 0;
                while (reader.Read()) count++;
                return count;
            }

            public void Dispose()
            {
                Db?.Dispose();
                _file.Dispose();
            }
        }

        private static readonly string[] Queries =
        {
            "SELECT $ FROM docs",
            "SELECT $ FROM docs WHERE $.CustomerIdentifierNumber > 50",
            "SELECT $ FROM docs WHERE $.OrderHeaderInformation.InnerIdentifier = 3",
            "SELECT $ FROM docs WHERE $.OrderHeaderInformation.InnerNestedDocument.DeepValue >= 2",
            "SELECT $ FROM docs WHERE $.Tags[*] ANY = 't1'",
            "SELECT $ FROM docs WHERE $.OrderLineItemCollection[*].Quantity ANY > 5",
            "SELECT $ FROM docs WHERE $.ScalarPropertyNumber3 = null",
            "SELECT $ FROM docs WHERE $.ScalarPropertyNumber4 > 0",
            "SELECT $ FROM docs WHERE $.fieldnamewithcase < 50",
            "SELECT $ FROM docs WHERE $.EmptyDocumentField = {}",
            "SELECT $ FROM docs WHERE $.ExplicitNullField = null AND $.OptionalMetadataPayload != null",
            "SELECT $.CustomerIdentifierNumber FROM docs",
            "SELECT $.OrderHeaderInformation FROM docs",
            "SELECT $.OrderHeaderInformation.InnerNestedDocument.DeepOther FROM docs",
            "SELECT { a: $.CustomerIdentifierNumber, b: $.OrderHeaderInformation.InnerDescription, c: $.ScalarPropertyNumber0 } FROM docs",
            "SELECT { id: $._id, n: $.CustomerDisplayNameValue } FROM docs WHERE $.CustomerIdentifierNumber < 30",
            "SELECT { lines: $.OrderLineItemCollection[*].ProductCode, total: SUM($.OrderLineItemCollection[*].Quantity) } FROM docs",
            "SELECT { c: COUNT($.Labels), m: $.MeasurementSeriesValues[3] } FROM docs WHERE $.MeasurementSeriesValues != null",
            "SELECT { k: @key, n: COUNT(*) } FROM docs GROUP BY $.CustomerDisplayNameValue",
            "SELECT $ FROM docs ORDER BY $.CustomerIdentifierNumber, $._id",
            "SELECT $ FROM docs ORDER BY $.ScalarPropertyNumber1 DESC, $._id LIMIT 7",
            "SELECT $.ScalarPropertyNumber2 FROM docs ORDER BY $._id DESC",
            "SELECT COUNT(*) FROM docs",
            "SELECT $ FROM docs WHERE $.DeepStructureRoot.LevelNumber = 69",
            "SELECT $ FROM docs WHERE $.Int32FieldValue > 0 AND $.DoubleFieldValue = 0",
        };

        private List<string> CompareStores(Store baseline, Store candidate, string phase)
        {
            var diffs = new List<string>();
            foreach (var collection in baseline.Db.GetCollectionNames().OrderBy(x => x))
            {
                var expected = baseline.Db.GetCollection(collection).FindAll().Select(d => (BsonValue)d).ToList();
                var actual = candidate.Db.GetCollection(collection).FindAll().Select(d => (BsonValue)d).ToList();
                diffs.AddRange(CompareLists(expected, actual, $"{phase}/{candidate.Mode}/{collection}/FindAll", ordered: true));
            }
            foreach (var sql in Queries)
            {
                List<BsonValue> expected, actual;
                try { expected = Sql(baseline.Db, sql); }
                catch (Exception ex) { expected = new List<BsonValue> { "ERR " + ex.GetType().Name + ": " + ex.Message }; }
                try { actual = Sql(candidate.Db, sql); }
                catch (Exception ex) { actual = new List<BsonValue> { "ERR " + ex.GetType().Name + ": " + ex.Message }; }
                var ordered = sql.Contains("ORDER BY");
                diffs.AddRange(CompareLists(expected, actual, $"{phase}/{candidate.Mode}/{sql}", ordered));
            }
            return diffs;
        }

        private void AssertEquivalent(Store baseline, IEnumerable<Store> candidates, string phase)
        {
            var diffs = candidates.SelectMany(c => CompareStores(baseline, c, phase)).ToList();
            foreach (var diff in diffs.Take(40)) _output.WriteLine(diff);
            if (DeferFailures) { _deferred.AddRange(diffs); return; }
            diffs.Should().BeEmpty($"phase '{phase}' must match the Legacy BSON store");
        }

        private readonly List<string> _deferred = new List<string>();
        private static readonly bool DeferFailures = Environment.GetEnvironmentVariable("LITEDB_DIFF_DEFER") == "1";

        private static void Apply(IEnumerable<Store> stores, Action<LiteDatabase> action)
        {
            foreach (var store in stores) action(store.Db);
        }

        [Fact]
        public void Compact_and_auto_databases_match_legacy_through_mutations_queries_indexes_reopen_and_rebuild()
        {
            using var legacy = new Store(CompactStorageMode.Legacy);
            using var compact = new Store(CompactStorageMode.Compact);
            using var auto = new Store(CompactStorageMode.Auto);
            var all = new[] { legacy, compact, auto };
            var candidates = new[] { compact, auto };

            // Phase 1: bulk insert of every shape.
            Apply(all, db => db.GetCollection("docs").InsertBulk(Generate(1, 180)));
            compact.SchemaPages().Should().BeGreaterThan(0, "the sweep must actually exercise compact schemas");
            auto.SchemaPages().Should().BeGreaterThan(0);
            legacy.SchemaPages().Should().Be(0);
            AssertEquivalent(legacy, candidates, "insert");

            // Phase 2: shape-changing updates (grow, shrink, type changes, compact<->BSON).
            Apply(all, db =>
            {
                var col = db.GetCollection("docs");
                var r = new Random(2);
                foreach (var i in Enumerable.Range(0, 180).Where(i => i % 3 == 0))
                {
                    var replacement = Shape(r, Id(i), (i / 2 + 4) % 9);
                    if (i % 4 == 0) replacement["UniqueDynamicName" + i] = "forces a new shape " + i;
                    col.Update(replacement).Should().BeTrue();
                }
                foreach (var i in Enumerable.Range(0, 180).Where(i => i % 5 == 1))
                {
                    var existing = col.FindById(Id(i));
                    existing.Remove(existing.Keys.Where(k => k != "_id").FirstOrDefault() ?? "none");
                    existing["AddedAfterInsertField"] = new BsonArray(Enumerable.Range(0, 50).Select(x => new BsonValue(x)));
                    col.Update(existing).Should().BeTrue();
                }
            });
            AssertEquivalent(legacy, candidates, "update");

            // Phase 3: delete then re-insert the same ids with different shapes in one transaction.
            Apply(all, db =>
            {
                var col = db.GetCollection("docs");
                db.BeginTrans().Should().BeTrue();
                var r = new Random(3);
                foreach (var i in Enumerable.Range(0, 180).Where(i => i % 7 == 2))
                {
                    col.Delete(Id(i)).Should().BeTrue();
                    col.Insert(Shape(r, Id(i), (i + 1) % 9));
                }
                db.Commit().Should().BeTrue();
            });
            AssertEquivalent(legacy, candidates, "delete-reinsert");

            // Phase 4: rolled-back transaction creating new shapes and updating documents.
            Apply(all, db =>
            {
                var col = db.GetCollection("docs");
                db.BeginTrans().Should().BeTrue();
                col.InsertBulk(Enumerable.Range(1000, 40).Select(i =>
                {
                    var d = new BsonDocument { ["_id"] = i };
                    for (var f = 0; f < 10; f++) d["RolledBackShapeField" + f] = i;
                    return d;
                }));
                col.Update(Shape(new Random(4), Id(10), 2));
                db.Rollback().Should().BeTrue();
                // New shape after rollback must not reference rolled-back schema IDs.
                col.InsertBulk(Enumerable.Range(2000, 10).Select(i =>
                {
                    var d = new BsonDocument { ["_id"] = i };
                    for (var f = 0; f < 10; f++) d["AfterRollbackShapeField" + f] = i;
                    d["Nested"] = new BsonDocument { ["RolledBackShapeField1"] = i, ["RolledBackShapeField2"] = i };
                    return d;
                }));
            });
            AssertEquivalent(legacy, candidates, "rollback");

            // Phase 5: secondary indexes on compact fields and multi-key paths.
            Apply(all, db =>
            {
                var col = db.GetCollection("docs");
                col.EnsureIndex("inner", "$.OrderHeaderInformation.InnerIdentifier");
                col.EnsureIndex("tags", "$.Tags[*]");
                col.EnsureIndex("name", "LOWER($.CustomerDisplayNameValue)");
                col.EnsureIndex("customer", "$.CustomerIdentifierNumber");
            });
            AssertEquivalent(legacy, candidates, "indexes");
            foreach (var store in all)
            {
                var col = store.Db.GetCollection("docs");
                col.Count(Query.EQ("$.OrderHeaderInformation.InnerIdentifier", 3)).Should()
                    .Be(legacy.Db.GetCollection("docs").Count(Query.EQ("$.OrderHeaderInformation.InnerIdentifier", 3)));
                col.Count("$.Tags[*] ANY = 't2'").Should()
                    .Be(legacy.Db.GetCollection("docs").Count("$.Tags[*] ANY = 't2'"));
            }

            // Phase 6: second collection, rename, checkpoint and reopen.
            Apply(all, db =>
            {
                db.GetCollection("other").InsertBulk(Generate(5, 60, 5000));
                db.RenameCollection("other", "renamed").Should().BeTrue();
                db.Checkpoint();
            });
            foreach (var store in all) store.Reopen();
            AssertEquivalent(legacy, candidates, "reopen");

            // Phase 7: explicit rebuild in each mode preserves every document.
            foreach (var mode in new[] { CompactStorageMode.Legacy, CompactStorageMode.Compact, CompactStorageMode.Auto })
            {
                foreach (var store in candidates) store.Db.Rebuild(new RebuildOptions { CompactStorage = mode });
                AssertEquivalent(legacy, candidates, "rebuild-" + mode);
            }
            _deferred.Should().BeEmpty();
        }

        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(13)]
        public void Random_operation_sequences_with_safepoints_rollbacks_and_reopen_match_legacy(int seed)
        {
            var stores = new[] { CompactStorageMode.Legacy, CompactStorageMode.Auto, CompactStorageMode.Compact }
                .Select(mode => new SmallPageStore(mode)).ToArray();
            try
            {
                var legacy = stores[0].Store;
                var candidates = stores.Skip(1).Select(s => s.Store).ToArray();
                var r = new Random(seed);
                var nextId = 0;
                for (var step = 0; step < 60; step++)
                {
                    var op = r.Next(10);
                    if ((op == 3 || op == 4) && nextId == 0) op = 0;
                    var opSeed = r.Next();
                    var count = r.Next(1, 12);
                    var baseId = nextId;
                    if (op <= 2) nextId += count;
                    var target = nextId == 0 ? 0 : r.Next(nextId);
                    var shape = r.Next(9);
                    var rollback = r.Next(4) == 0;
                    var threshold = r.Next(100);
                    var outcomes = new List<string>();
                    foreach (var store in stores.Select(s => s.Store))
                    {
                        var db = store.Db;
                        var col = db.GetCollection("docs");
                        var local = new Random(opSeed);
                        var explicitTransaction = op >= 3 && op <= 5;
                        if (explicitTransaction) db.BeginTrans();
                        try
                        {
                            switch (op)
                            {
                                case 0:
                                case 1:
                                case 2:
                                    col.InsertBulk(Enumerable.Range(baseId, count).Select(i => Shape(local, Id(i), (i / 2 + shape) % 9)).ToList());
                                    break;
                                case 3:
                                    for (var i = 0; i < count; i++) col.Upsert(Shape(local, Id((target + i) % Math.Max(1, nextId)), (shape + i) % 9));
                                    break;
                                case 4:
                                    col.Delete(Id(target));
                                    col.Insert(Shape(local, Id(target), shape));
                                    break;
                                case 5:
                                    db.Execute($"UPDATE docs SET {{ ScalarPropertyNumber0: 'updated {opSeed}', AddedByExpression: [1, 2, {{ Nested: $._id }}] }} WHERE $.CustomerIdentifierNumber < {threshold}");
                                    break;
                                case 6:
                                    col.DeleteMany($"$.CustomerIdentifierNumber > {90 + shape}");
                                    break;
                                case 7:
                                    db.Checkpoint();
                                    break;
                                case 8:
                                    store.Reopen();
                                    break;
                                default:
                                    // Vector values are not valid ordinary index keys in any mode; index vector-free paths.
                                    col.EnsureIndex("idx" + shape % 3, new[] { "$.CustomerIdentifierNumber", "LOWER($.CustomerDisplayNameValue)", "$.OrderHeaderInformation.InnerIdentifier" }[shape % 3]);
                                    break;
                            }
                            if (explicitTransaction)
                            {
                                if (rollback) db.Rollback(); else db.Commit();
                            }
                            outcomes.Add("ok");
                        }
                        catch (Exception ex)
                        {
                            if (explicitTransaction) db.Rollback();
                            outcomes.Add(ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                    outcomes.Distinct().Should().HaveCount(1, $"step {step} op {op} must behave identically in every mode: {string.Join(" | ", outcomes)}");
                }
                AssertEquivalent(legacy, candidates, $"random-{seed}");
                foreach (var store in stores.Select(s => s.Store)) store.Reopen();
                AssertEquivalent(legacy, candidates, $"random-{seed}-reopen");
                _deferred.Should().BeEmpty();
            }
            finally
            {
                foreach (var store in stores) store.Dispose();
            }
        }

        private sealed class SmallPageStore : IDisposable
        {
            public Store Store { get; }

            public SmallPageStore(CompactStorageMode mode)
            {
                Store = new Store(mode, transactionPageLimit: 3);
            }

            public void Dispose() => Store.Dispose();
        }

        [Fact]
        public void Same_transaction_reads_updates_and_includes_documents_that_reference_its_own_new_schemas()
        {
            var stores = new[] { CompactStorageMode.Legacy, CompactStorageMode.Auto }.Select(m => new Store(m, transactionPageLimit: 3)).ToArray();
            try
            {
                var inside = new List<List<BsonValue>>();
                foreach (var store in stores)
                {
                    var db = store.Db;
                    db.BeginTrans().Should().BeTrue();
                    db.GetCollection("refs").InsertBulk(Generate(21, 40, 7000));
                    db.GetCollection("docs").InsertBulk(Enumerable.Range(0, 40).Select(i =>
                    {
                        var doc = Shape(new Random(i), Id(i), 2);
                        doc["Reference"] = new BsonDocument { ["$id"] = Id(7000 + i), ["$ref"] = "refs" };
                        return doc;
                    }));
                    db.Execute("UPDATE docs SET { OrderHeaderInformation: { InnerIdentifier: $.CustomerIdentifierNumber, Replaced: true }, NewlyAddedFieldName: [$.Tags, $._id] } WHERE $.CustomerIdentifierNumber >= 20");
                    var rows = Sql(db, "SELECT $ FROM docs INCLUDE $.Reference");
                    rows.AddRange(Sql(db, "SELECT { r: $.Reference.CustomerIdentifierNumber, n: $.NewlyAddedFieldName } FROM docs INCLUDE $.Reference"));
                    inside.Add(rows);
                    db.Commit().Should().BeTrue();
                }
                CompareLists(inside[0], inside[1], "in-transaction", ordered: true).Should().BeEmpty();
                AssertEquivalent(stores[0], stores.Skip(1), "after-commit");
                _deferred.Should().BeEmpty();
            }
            finally
            {
                foreach (var store in stores) store.Dispose();
            }
        }

        [Fact]
        public void Mapper_round_trips_match_between_legacy_and_auto_storage()
        {
            var mapper = new BsonMapper();
            var entities = Enumerable.Range(1, 30).Select(i => new MappedEntity
            {
                Id = i,
                Name = "entity " + i,
                LocalWhen = new DateTime(2026, 9, 23, 10, 11, 12, 13, DateTimeKind.Local).AddDays(i),
                UtcWhen = new DateTime(2026, 9, 23, 10, 11, 12, 13, DateTimeKind.Utc).AddHours(i),
                Amount = 1.10m * i,
                Ratio = i / 3.0,
                Key = new Guid(i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10),
                Blob = Enumerable.Range(0, i).Select(x => (byte)x).ToArray(),
                Tags = Enumerable.Range(0, i % 5).Select(x => "tag" + x).ToList(),
                Children = Enumerable.Range(0, i % 3).Select(x => new MappedChild { Code = "c" + x, Quantity = x }).ToList(),
                Extra = new Dictionary<string, object> { ["alpha"] = i, ["beta"] = "b" + i },
                Optional = i % 2 == 0 ? (int?)i : null,
                Kind = (MappedKind)(i % 3)
            }).ToList();

            var results = new Dictionary<CompactStorageMode, List<BsonDocument>>();
            foreach (var mode in new[] { CompactStorageMode.Legacy, CompactStorageMode.Auto })
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", CompactStorage = mode }, mapper);
                var col = db.GetCollection<MappedEntity>("entities");
                col.InsertBulk(entities);
                results[mode] = col.FindAll().Select(e => mapper.ToDocument(e)).ToList();
                col.Query().Where(e => e.Amount > 5m && e.Tags.Count > 1).Select(e => e.Id).ToList().Should()
                    .Equal(entities.Where(e => e.Amount > 5m && e.Tags.Count > 1).Select(e => e.Id));
            }
            var diffs = CompareLists(results[CompactStorageMode.Legacy].Cast<BsonValue>().ToList(),
                results[CompactStorageMode.Auto].Cast<BsonValue>().ToList(), "mapper", ordered: true);
            diffs.Should().BeEmpty();
            var bson = BsonSerializer.Serialize(mapper.ToDocument(entities[0]));
            BitConverter.ToInt32(bson, 0).Should().Be(bson.Length, "public BsonSerializer output must stay plain BSON");
        }

        public enum MappedKind { First, Second, Third }

        public class MappedChild
        {
            public string Code { get; set; }
            public int Quantity { get; set; }
        }

        public class MappedEntity
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public DateTime LocalWhen { get; set; }
            public DateTime UtcWhen { get; set; }
            public decimal Amount { get; set; }
            public double Ratio { get; set; }
            public Guid Key { get; set; }
            public byte[] Blob { get; set; }
            public List<string> Tags { get; set; }
            public List<MappedChild> Children { get; set; }
            public Dictionary<string, object> Extra { get; set; }
            public int? Optional { get; set; }
            public MappedKind Kind { get; set; }
        }
    }
}
