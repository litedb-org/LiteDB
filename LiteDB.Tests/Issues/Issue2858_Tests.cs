using System;
using System.Globalization;
using System.IO;
using System.Linq;

using FluentAssertions;

using Xunit;

namespace LiteDB.Tests.Issues
{
    [CollectionDefinition("Issue2858Culture", DisableParallelization = true)]
    public sealed class Issue2858CultureCollection
    {
    }

    [Collection("Issue2858Culture")]
    public class Issue2858_Tests
    {
        [Fact]
        public void Lowercase_insert_keyword_executes_under_Turkish_culture()
        {
            RunUnderTurkishCulture(directory =>
            {
                var path = Path.Combine(directory, "insert-keyword.db");

                using (var database = CreateDatabase(path))
                {
                    ExecuteScalar(database,
                        "INSERT INTO uppercase_control:INT VALUES { marker: 'uppercase' }")
                        .AsInt32.Should().Be(1);
                    AssertInserted(database, "uppercase_control", "uppercase");

                    ExecuteScalar(database,
                        "insert into lowercase_target:INT values { marker: 'lowercase' }")
                        .AsInt32.Should().Be(1);
                    AssertInserted(database, "lowercase_target", "lowercase");
                }

                using var reopened = new LiteDatabase(path);
                AssertInserted(reopened, "uppercase_control", "uppercase");
                AssertInserted(reopened, "lowercase_target", "lowercase");
            });
        }

        [Fact]
        public void Lowercase_insert_auto_id_type_executes_under_Turkish_culture()
        {
            RunUnderTurkishCulture(directory =>
            {
                var path = Path.Combine(directory, "insert-auto-id.db");

                using (var database = CreateDatabase(path))
                {
                    ExecuteScalar(database,
                        "INSERT INTO uppercase_control:INT VALUES { marker: 'uppercase' }")
                        .AsInt32.Should().Be(1);
                    AssertInserted(database, "uppercase_control", "uppercase");

                    ExecuteScalar(database,
                        "INSERT into lowercase_target:int values { marker: 'lowercase' }")
                        .AsInt32.Should().Be(1);
                    AssertInserted(database, "lowercase_target", "lowercase");
                }

                using var reopened = new LiteDatabase(path);
                AssertInserted(reopened, "uppercase_control", "uppercase");
                AssertInserted(reopened, "lowercase_target", "lowercase");
            });
        }

        [Fact]
        public void Lowercase_rebuild_executes_and_persists_its_options_under_Turkish_culture()
        {
            RunUnderTurkishCulture(directory =>
            {
                AssertRebuild(Path.Combine(directory, "uppercase-rebuild.db"), "REBUILD");
                AssertRebuild(Path.Combine(directory, "lowercase-rebuild.db"), "rebuild");
            });
        }

        [Fact]
        public void Lowercase_commit_closes_and_durably_commits_the_transaction_under_Turkish_culture()
        {
            RunUnderTurkishCulture(directory =>
            {
                var path = Path.Combine(directory, "commit.db");
                using (CreateDatabase(path))
                {
                }

                AssertCommit(path, "COMMIT", 1, "uppercase");
                AssertCommit(path, "commit", 2, "lowercase");

                using var reopened = new LiteDatabase(path);
                var rows = reopened.GetCollection("transactions").FindAll().ToArray();
                rows.Select(row => row["_id"].AsInt32).Should().Equal(1, 2);
                rows.Select(row => row["marker"].AsString).Should().Equal("uppercase", "lowercase");
            });
        }

        private static void AssertRebuild(string path, string keyword)
        {
            // Leave the connection's collation unset: an explicit connection collation is
            // intentionally required to keep matching the header after a rebuild changes it.
            using (var database = new LiteDatabase(path))
            {
                var rows = database.GetCollection("rows");
                rows.Insert(new[]
                {
                    new BsonDocument { ["_id"] = 1, ["name"] = "Alpha", ["marker"] = "first" },
                    new BsonDocument { ["_id"] = 2, ["name"] = "beta", ["marker"] = "second" }
                });
                rows.EnsureIndex("name", true).Should().BeTrue();
                database.UserVersion = 2858;
                database.Checkpoint();

                database.Collation.SortOptions.Should().Be(CompareOptions.IgnoreCase);

                ExecuteScalar(database, keyword + " { collation: 'en-US/None' }")
                    .Type.Should().Be(BsonType.Int32);
                AssertRebuiltState(database);
            }

            using var reopened = new LiteDatabase(path);
            AssertRebuiltState(reopened);
        }

        private static void AssertRebuiltState(LiteDatabase database)
        {
            database.Collation.ToString().Should().Be("en-US/None");
            database.UserVersion.Should().Be(2858);

            var rows = database.GetCollection("rows");
            rows.Count().Should().Be(2);
            rows.FindById(1)["marker"].AsString.Should().Be("first");
            rows.FindById(2)["marker"].AsString.Should().Be("second");

            rows.EnsureIndex("name", true).Should().BeFalse("REBUILD must preserve the unique index");
            rows.Find(Query.EQ("name", "Alpha")).Single()["_id"].AsInt32.Should().Be(1);
            rows.Find(Query.EQ("name", "ALPHA")).Should().BeEmpty(
                "the persisted None collation must be observably case-sensitive");
        }

        private static void AssertCommit(string path, string keyword, int id, string marker)
        {
            using (var database = new LiteDatabase(path))
            {
                database.BeginTrans().Should().BeTrue();
                database.GetCollection("transactions").Insert(new BsonDocument
                {
                    ["_id"] = id,
                    ["marker"] = marker
                });

                ExecuteScalar(database, keyword).AsBoolean.Should().BeTrue();
                database.Rollback().Should().BeFalse("COMMIT must close the active transaction");
            }

            using var reopened = new LiteDatabase(path);
            var row = reopened.GetCollection("transactions").FindById(id);
            Assert.NotNull(row);
            row["marker"].AsString.Should().Be(marker);
        }

        private static void AssertInserted(LiteDatabase database, string collection, string marker)
        {
            var rows = database.GetCollection(collection).FindAll().ToArray();
            rows.Should().ContainSingle("INSERT must execute exactly once");
            rows[0]["_id"].Type.Should().Be(BsonType.Int32);
            rows[0]["_id"].AsInt32.Should().Be(1);
            rows[0]["marker"].AsString.Should().Be(marker);
        }

        private static BsonValue ExecuteScalar(LiteDatabase database, string sql)
        {
            return database.Execute(sql).Single();
        }

        private static LiteDatabase CreateDatabase(string path)
        {
            return new LiteDatabase(new ConnectionString
            {
                Filename = path,
                Collation = Collation.Binary
            });
        }

        private static void RunUnderTurkishCulture(Action<string> assertion)
        {
            var previousCulture = CultureInfo.CurrentCulture;
            var previousUiCulture = CultureInfo.CurrentUICulture;
            var directory = Path.Combine(Path.GetTempPath(), "litedb-2858-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                var turkish = CultureInfo.GetCultureInfo("tr-TR");
                CultureInfo.CurrentCulture = turkish;
                CultureInfo.CurrentUICulture = turkish;

                "insert".ToUpper().Should().Be("İNSERT");
                "int".ToUpper().Should().Be("İNT");
                "rebuild".ToUpper().Should().Be("REBUİLD");
                "commit".ToUpper().Should().Be("COMMİT");

                assertion(directory);
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
                Directory.Delete(directory, true);
            }
        }
    }
}
