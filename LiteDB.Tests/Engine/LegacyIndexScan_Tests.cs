using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A read-only open of a file awaiting the v11 index migration fails by default.
    /// <c>legacy index scan=true</c> opens it unchanged and answers every query with
    /// full scans, so results must equal those of a migrated copy using its indexes.
    /// </summary>
    public class LegacyIndexScan_Tests
    {
        private static readonly Func<BsonValue, BsonExpression>[] PeoplePredicates =
        {
            _ => Query.EQ("name", "a"),
            _ => Query.EQ("name", "A"),
            _ => Query.GT("name", "a"),
            _ => Query.Between("name", "a", "b"),
            _ => Query.StartsWith("name", "a"),
            _ => Query.In("name", "B", "é"),
            _ => Query.EQ("person", new BsonDocument { ["first"] = "B", ["age"] = 1 }),
            _ => Query.GT("person", new BsonDocument { ["first"] = "a", ["age"] = 0 }),
            _ => Query.EQ("score", 1.5),
            _ => Query.EQ("score", 2.25),
            _ => Query.EQ("score", 0.1),
            _ => Query.EQ("score", 0.1m),
            _ => Query.GT("score", 1),
            _ => Query.LT("score", 2.25m),
            _ => BsonExpression.Create("$.tags[*] ANY = 'a'"),
            _ => Query.EQ("email", "user5@example.com"),
            _ => BsonExpression.Create("LOWER($.name) = 'a'"),
            id => Query.EQ("_id", id),
            id => Query.GT("_id", id),
            id => Query.LTE("_id", id)
        };

        private static readonly string[] Orders = { "$._id", "$.name", "$.score", "$.person", "LOWER($.name)" };

        [Theory]
        [InlineData(null)]
        [InlineData(LegacyIndexFixtures.Password)]
        public void Read_only_legacy_open_fails_without_the_switch_and_names_it(string password)
        {
            using var file = LegacyIndexFixtures.Extract("indexes", password);
            var before = File.ReadAllBytes(file.Filename);

            Action open = () => new LiteDatabase(Connection(file.Filename, password, legacyIndexScan: false)).Dispose();

            open.Should().Throw<LiteException>().WithMessage("*requires migration*legacy index scan=true*");
            File.ReadAllBytes(file.Filename).Should().Equal(before);
        }

        [Theory]
        [InlineData(null, "direct")]
        [InlineData(LegacyIndexFixtures.Password, "direct")]
        [InlineData(null, "shared")]
        public void Legacy_index_scan_returns_the_results_of_the_migrated_indexes(string password, string connection)
        {
            using var legacy = LegacyIndexFixtures.Extract("indexes", password);
            using var migrated = LegacyIndexFixtures.Extract("indexes", password);
            var before = File.ReadAllBytes(legacy.Filename);
            using (var db = new LiteDatabase(new ConnectionString { Filename = migrated.Filename, Password = password }))
                db.Checkpoint();

            var cs = Connection(legacy.Filename, password, legacyIndexScan: true);
            cs.Connection = connection == "shared" ? ConnectionType.Shared : ConnectionType.Direct;
            using (var scan = new LiteDatabase(cs))
            using (var seek = new LiteDatabase(new ConnectionString { Filename = migrated.Filename, Password = password, ReadOnly = true }))
            {
                foreach (var name in new[] { "people", "numbers" })
                    ShouldAnswerAlike(scan.GetCollection(name), seek.GetCollection(name));
                scan.GetCollection("people").Query().Where("$.name = 'a'").GetPlan()["index"]["mode"].AsString
                    .Should().StartWith("FULL INDEX SCAN(_id)", "stale skip lists must not be seeked");
            }

            File.ReadAllBytes(legacy.Filename).Should().Equal(before, "a read-only scan must not change the file");
            File.Exists(FileHelper.GetLogFile(legacy.Filename)).Should().BeFalse();
        }

        [Fact]
        public void Includes_find_referenced_documents_by_scanning()
        {
            using var file = new TempFile();
            ObjectId id;
            using (var db = new LiteDatabase(file.Filename))
            {
                var people = db.GetCollection("people");
                id = people.Insert(new BsonDocument { ["name"] = "a" }).AsObjectId;
                people.Insert(new BsonDocument { ["name"] = "b" });
                db.GetCollection("orders").Insert(new BsonDocument
                {
                    ["_id"] = 1, ["person"] = new BsonDocument { ["$id"] = id, ["$ref"] = "people" }
                });
                db.GetCollection("orders").Insert(new BsonDocument
                {
                    ["_id"] = 2, ["person"] = new BsonDocument { ["$id"] = ObjectId.NewObjectId(), ["$ref"] = "people" }
                });
            }
            MarkLegacy(file.Filename);

            using var scan = new LiteDatabase(Connection(file.Filename, null, legacyIndexScan: true));
            var orders = scan.GetCollection("orders").Include("$.person").FindAll().OrderBy(x => x["_id"].AsInt32).ToList();

            orders[0]["person"]["name"].AsString.Should().Be("a");
            orders[1]["person"]["$missing"].AsBoolean.Should().BeTrue();
        }

        [Fact]
        public void Writable_opens_ignore_the_switch_and_migrate()
        {
            using var file = LegacyIndexFixtures.Extract("indexes", null);
            var cs = Connection(file.Filename, null, legacyIndexScan: true);
            cs.ReadOnly = false;

            using (var db = new LiteDatabase(cs))
                db.GetCollection("people").Count(Query.EQ("name", "a")).Should().Be(8);

            IndexMigration_Tests.ReadHeader(file.Filename, null)[EnginePragmas.P_INDEX_ORDER_VERSION].Should().Be(EnginePragmas.INDEX_ORDER_VERSION);
        }

        [Fact]
        public void Connection_string_round_trips_the_switch()
        {
            var parsed = new ConnectionString("legacy index scan=true;filename=data.db;readonly=true");
            parsed.LegacyIndexScan.Should().BeTrue();
            new ConnectionString(parsed.ToString()).LegacyIndexScan.Should().BeTrue();
            new ConnectionString("filename=data.db").LegacyIndexScan.Should().BeFalse();
        }

        private static void ShouldAnswerAlike(ILiteCollection<BsonDocument> scan, ILiteCollection<BsonDocument> seek)
        {
            var ids = seek.FindAll().Select(x => x["_id"]).ToList();
            ids.Should().NotBeEmpty();
            Text(scan.FindAll()).Should().BeEquivalentTo(Text(seek.FindAll()));
            scan.Count().Should().Be(seek.Count());
            scan.Min().ToString().Should().Be(seek.Min().ToString());
            scan.Max().ToString().Should().Be(seek.Max().ToString());
            foreach (var id in ids.Where((_, i) => i % 7 == 0))
            {
                scan.FindById(id).ToString().Should().Be(seek.FindById(id).ToString());
                foreach (var predicate in PeoplePredicates)
                {
                    var expression = predicate(id);
                    var expected = Text(seek.Find(expression));
                    Text(scan.Find(expression)).Should().BeEquivalentTo(expected, expression.Source);
                    scan.Exists(expression).Should().Be(expected.Count > 0, expression.Source);
                    scan.Count(expression).Should().Be(expected.Count, expression.Source);
                }
            }
            foreach (var order in Orders)
            {
                OrderedKeys(scan, order, Query.Ascending).Should().Equal(OrderedKeys(seek, order, Query.Ascending), order);
                OrderedKeys(scan, order, Query.Descending).Should().Equal(OrderedKeys(seek, order, Query.Descending), order);
            }
            scan.Query().GroupBy("$.name").Select("{ key: @key, n: COUNT(*) }").ToList().Select(x => x.ToString())
                .Should().Equal(seek.Query().GroupBy("$.name").Select("{ key: @key, n: COUNT(*) }").ToList().Select(x => x.ToString()));
        }

        private static List<string> Text(IEnumerable<BsonDocument> documents) => documents.Select(x => x.ToString()).ToList();

        private static List<string> OrderedKeys(ILiteCollection<BsonDocument> collection, string order, int direction)
        {
            var query = collection.Query();
            var ordered = direction == Query.Ascending ? query.OrderBy(order) : query.OrderByDescending(order);
            // Ties may keep a different scan order, so compare the ordered keys.
            var key = BsonExpression.Create(order);
            return ordered.ToList().Select(x => key.ExecuteScalar(x).ToString()).ToList();
        }

        private static ConnectionString Connection(string file, string password, bool legacyIndexScan) =>
            new ConnectionString { Filename = file, Password = password, ReadOnly = true, LegacyIndexScan = legacyIndexScan };

        private static void MarkLegacy(string file) =>
            IndexMigration_Tests.RewriteHeaders(file, null, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = HeaderPage.CHECKSUM_FILE_VERSION;
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
                Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
            });
    }
}
