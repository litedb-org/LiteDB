using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class FullDocumentScanLifetime_Tests
    {
        [Theory]
        [InlineData(CompactStorageMode.Legacy, false, 8)]
        [InlineData(CompactStorageMode.Legacy, false, 40)]
        [InlineData(CompactStorageMode.Legacy, true, 40)]
        [InlineData(CompactStorageMode.Compact, false, 40)]
        [InlineData(CompactStorageMode.Auto, false, 72)]
        public void Cursor_disposal_page_pressure_and_rollback_preserve_owned_results_and_reopen(
            CompactStorageMode mode, bool forUpdate, int take)
        {
            using var file = new TempFile();
            var connection = new ConnectionString
            {
                Filename = file.Filename, CompactStorage = mode,
                CacheSize = 65536, TransactionPageLimit = 1
            };
            var documents = FullDocumentScan_Tests.Documents();
            foreach (var doc in documents)
                doc["Payload"] = new string((char)('a' + doc["Score"].AsInt32 % 20), 20_000);

            BsonDocument retained;
            using (var db = new LiteDatabase(connection))
            {
                db.GetCollection("owners").Insert(new BsonDocument { ["_id"] = 1, ["Name"] = "resolved" });
                var rows = db.GetCollection("rows");
                rows.InsertBulk(documents);
                rows.EnsureIndex("score", "Score");
                db.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 1, ["Value"] = "untouched" });
                db.Checkpoint();
                var before = TempFile.ReadAllBytesShared(file.Filename);
                var expected = rows.Query().Include("Ref").ToArray().Select(d => d.ToString()).ToArray();

                db.BeginTrans().Should().BeTrue();
                var query = rows.Query().Include("Ref").Where("Nested.Value != null");
                BorrowedQueryDiagnostics.Reset();
                var results = forUpdate ? query.ForUpdate().ToEnumerable() : query.ToEnumerable();
                using (var cursor = results.GetEnumerator())
                {
                    retained = null;
                    for (var i = 0; i < take; i++)
                    {
                        cursor.MoveNext().Should().BeTrue();
                        cursor.Current.ToString().Should().Be(expected[i]);
                        cursor.Current["Ref"]["Name"].AsString.Should().Be("resolved");
                        retained ??= cursor.Current;
                    }
                }
                BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(forUpdate ? take : Math.Min(32, take));
                BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(2 * take, "each row and its reference are loaded once");
                TempFile.ReadAllBytesShared(file.Filename).Should().Equal(before, "a scan must not mutate stored bytes");
                rows.UpdateMany("{ Payload: 'replacement' }", "Score >= 1").Should().Be(72);
                db.Rollback().Should().BeTrue("disposing the cursor must preserve the caller's transaction");
                rows.Query().Where("Nested.Value != null").ToArray().Select(d => d.ToString())
                    .Should().Equal(documents.Select(d => d.ToString()));
                db.Checkpoint();
                retained.ToString().Should().Be(expected[0], "results own their strings and nested values");
            }

            connection.ReadOnly = true;
            var stable = TempFile.ReadAllBytesShared(file.Filename);
            using (var db = new LiteDatabase(connection))
            {
                var rows = db.GetCollection("rows");
                rows.Query().Where("Nested.Value != null").ToArray().Select(d => d.ToString())
                    .Should().Equal(documents.Select(d => d.ToString()));
                rows.FindOne("Score = 72").ToString().Should().Be(documents[71].ToString());
                db.GetCollection("unrelated").FindById(1)["Value"].AsString.Should().Be("untouched");
            }
            TempFile.ReadAllBytesShared(file.Filename).Should().Equal(stable);
        }

        [Fact]
        public void Unicode_fallback_loads_each_document_once_and_owning_filter_keeps_late_rejections()
        {
            using var db = FullDocumentScan_Tests.Legacy();
            var rows = db.GetCollection("rows");
            var documents = FullDocumentScan_Tests.Documents();
            foreach (var doc in documents) doc["K"] = 5;
            rows.InsertBulk(documents);
            BorrowedQueryDiagnostics.Reset();
            rows.Query().Where("Nested.Value >= 0").ToArray().Select(d => d["_id"].AsInt32)
                .Should().Equal(Enumerable.Range(1, 32));
            BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(32);
            BorrowedQueryDiagnostics.BorrowedPredicateFallbacks.Should().Be(32);
            BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(72);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Key_moving_update_and_delete_keep_deduplication_and_rollback(bool indexed)
        {
            using var db = FullDocumentScan_Tests.Legacy();
            var rows = db.GetCollection("rows");
            rows.InsertBulk(FullDocumentScan_Tests.Documents());
            if (indexed) rows.EnsureIndex("score", "Score");
            db.BeginTrans().Should().BeTrue();
            BorrowedQueryDiagnostics.Reset();
            rows.UpdateMany("{ Score: Score + 100 }", "Score >= 1 AND Score < 200").Should().Be(72);
            if (!indexed) BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(72);
            rows.FindAll().Select(d => d["Score"].AsInt32).OrderBy(x => x).Should().Equal(Enumerable.Range(101, 72));
            rows.DeleteMany("Score >= 101").Should().Be(72);
            rows.Count().Should().Be(0);
            db.Rollback().Should().BeTrue();
            rows.FindAll().Select(d => d["Score"].AsInt32).OrderBy(x => x).Should().Equal(Enumerable.Range(1, 72));
        }
    }
}
