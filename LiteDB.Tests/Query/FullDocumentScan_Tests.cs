using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class FullDocumentScan_Tests
    {
        [Theory]
        [InlineData(-1, true)]
        [InlineData(0, false)]
        [InlineData(31, false)]
        public void Strategy_switches_only_after_an_entirely_matching_fixed_prefix(int rejection, bool owning)
        {
            var strategy = new FullDocumentScanStrategy();
            for (var i = 0; i < 32; i++)
            {
                strategy.UseOwning.Should().BeFalse();
                strategy.Observe(i != rejection);
            }
            strategy.UseOwning.Should().Be(owning);
            strategy.Observe(false);
            strategy.UseOwning.Should().Be(owning, "the decision is fixed after the probe");
        }

        [Theory]
        [InlineData("full", true)]
        [InlineData("projection", false)]
        [InlineData("aggregate", false)]
        [InlineData("count", false)]
        [InlineData("update", false)]
        [InlineData("includeBefore", false)]
        [InlineData("includeAfter", true)]
        [InlineData("offset", false)]
        [InlineData("sort", false)]
        [InlineData("group", false)]
        [InlineData("unsupported", false)]
        public void Strategy_admission_requires_unavoidable_full_materialization(string shape, bool eligible)
        {
            BorrowedPredicateEvaluator.TryCreate(new[] { BsonExpression.Create("Score >= 0") }, out var predicate);
            var plan = new QueryPlan("rows")
            {
                Fields = new HashSet<string>(), BorrowedFilter = predicate,
                Select = new Select(BsonExpression.Root, false)
            };
            switch (shape)
            {
                case "projection": plan.Fields.Add("Score"); break;
                case "aggregate": plan.Select = new Select(BsonExpression.Create("SUM(*.Score)"), true); break;
                case "count": plan.RowAggregate = RowAggregate.TryCreate(QueryAggregateExpressions.Count); break;
                case "update": plan.ForUpdate = true; break;
                case "includeBefore": plan.IncludeBefore.Add("Ref"); break;
                case "includeAfter": plan.IncludeAfter.Add("Ref"); break;
                case "offset": plan.Offset = 1; break;
                case "sort": plan.OrderBy = new OrderBy(new[] { new OrderByItem("Score", Query.Ascending) }); break;
                case "group": plan.GroupBy = new GroupBy("Score", "$", null, null); break;
                case "unsupported": plan.BorrowedFilter = null; break;
            }
            FullDocumentScanStrategy.CanUse(plan).Should().Be(eligible);
        }

        [Theory]
        [InlineData(CompactStorageMode.Legacy, "en-US/IgnoreCase")]
        [InlineData(CompactStorageMode.Legacy, "en-US/None")]
        [InlineData(CompactStorageMode.Compact, "en-US/IgnoreCase")]
        [InlineData(CompactStorageMode.Auto, "en-US/IgnoreCase")]
        public void Full_scan_and_borrowed_write_cursor_match_expression_oracle(CompactStorageMode mode, string collation)
        {
            using var db = new LiteDatabase(new ConnectionString
            {
                Filename = ":memory:", CompactStorage = mode, Collation = new Collation(collation)
            });
            var rows = db.GetCollection("rows");
            var documents = Documents();
            rows.InsertBulk(documents);
            if (mode == CompactStorageMode.Compact)
            {
                db.Execute("SELECT COUNT(*) FROM $dump WHERE $.pageType = 'Schema'")
                    .Single().AsDocument.Values.Single().AsInt32.Should().BeGreaterThan(0);
            }
            var parameters = new BsonDocument { ["low"] = 0, ["name"] = "London", ["container"] = new BsonDocument { ["v"] = 1 } };
            foreach (var text in new[]
            {
                "Score >= @low",
                "Score >= @low AND (Name = @name OR Score < 0)",
                "Score >= 0 AND (Score >= 0 OR Nested = @container)",
                "Score >= 0 AND Nested.Value >= 0",
                "Score >= 0 AND K = null"
            })
            {
                var predicate = BsonExpression.Create(text, parameters);
                var expected = documents.Where(d => predicate.ExecuteScalar(d, db.Collation).AsBoolean)
                    .Select(d => d.ToString()).ToArray();
                BorrowedQueryDiagnostics.Reset();
                var actual = rows.Query().Where(predicate).ToArray();
                actual.Select(d => d.ToString()).Should().Equal(expected, text);
                BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(32, text);
                BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(documents.Length, text);

                BorrowedQueryDiagnostics.Reset();
                rows.Query().Where(predicate).ForUpdate().ToArray().Select(d => d.ToString()).Should().Equal(expected, text);
                BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(documents.Length, text);
            }
        }

        [Fact]
        public void Rejected_prefix_keeps_selective_materialization_and_pagination()
        {
            using var db = Legacy();
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Documents());
            BorrowedQueryDiagnostics.Reset();
            rows.Query().Where("Score >= 60").Limit(3).ToArray()
                .Select(d => d["Score"].AsInt32).Should().Equal(60, 61, 62);
            BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(62);
            BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(3);

            BorrowedQueryDiagnostics.Reset();
            rows.Query().Where("Score >= 0").Limit(40).ToArray().Should().HaveCount(40);
            BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(32);
            BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(40);
            rows.Query().Where("Score >= 0").Offset(35).Limit(3).ToArray()
                .Select(d => d["Score"].AsInt32).Should().Equal(36, 37, 38);
        }

        [Theory]
        [InlineData(false, 8)]
        [InlineData(false, 32)]
        [InlineData(false, 33)]
        [InlineData(false, 40)]
        [InlineData(true, 8)]
        [InlineData(true, 32)]
        [InlineData(true, 33)]
        [InlineData(true, 40)]
        public void Projection_errors_around_the_switch_preserve_order_and_cleanup(bool forUpdate, int errorAt)
        {
            using var db = Legacy();
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Documents());
            var query = rows.Query().Where("Score >= 0").Select("{ doc: $, value: DECIMAL(1) / (Score - " + errorAt + ") }");
            if (forUpdate) query.ForUpdate();
            var visited = 0;
            BorrowedQueryDiagnostics.Reset();
            Action read = () =>
            {
                foreach (var row in query.ToEnumerable())
                {
                    row["doc"]["Score"].AsInt32.Should().Be(++visited);
                }
            };
            read.Should().Throw<DivideByZeroException>();
            visited.Should().Be(errorAt - 1);
            BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(forUpdate ? errorAt : Math.Min(32, errorAt));
            db.BeginTrans().Should().BeTrue();
            rows.UpdateMany("{ Score: Score + 1 }", "Score = 72").Should().Be(1);
            db.Rollback().Should().BeTrue();
            rows.FindById(72)["Score"].AsInt32.Should().Be(72);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Includes_run_on_the_correct_side_of_both_filter_strategies(bool forUpdate)
        {
            using var db = Legacy();
            db.GetCollection("owners").Insert(new BsonDocument { ["_id"] = 1, ["Score"] = 100, ["Name"] = "resolved" });
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Documents());
            var after = rows.Query().Include("Ref").Where("Score >= 0");
            after.GetPlan()["includeAfter"].AsArray.Count.Should().Be(1);
            BorrowedQueryDiagnostics.Reset();
            var result = forUpdate ? after.ForUpdate().ToArray() : after.ToArray();
            result.Should().HaveCount(72);
            result.Should().OnlyContain(d => d["Ref"]["Name"].AsString == "resolved");
            BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(forUpdate ? 72 : 32);

            var before = rows.Query().Include("Ref").Where("Ref.Score >= 100");
            before.GetPlan()["includeBefore"].AsArray.Count.Should().Be(1);
            BorrowedQueryDiagnostics.Reset();
            (forUpdate ? before.ForUpdate().ToArray() : before.ToArray()).Should().HaveCount(72);
            BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(0);
        }

        internal static LiteDatabase Legacy() => new LiteDatabase("Filename=:memory:;Compact Storage=Legacy");

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Multikey_index_membership_deduplicates_addresses_before_either_filter(bool forUpdate)
        {
            using var db = Legacy();
            var rows = db.GetCollection("rows");
            var documents = Documents();
            foreach (var document in documents) document["Tags"] = new BsonArray { 0, 1 };
            rows.InsertBulk(documents);
            rows.EnsureIndex("tags", "Tags[*]");
            var query = rows.Query().Where("Tags ANY >= 0 AND Score >= 0");
            query.GetPlan()["index"]["name"].AsString.Should().Be("tags");
            BorrowedQueryDiagnostics.Reset();
            var actual = forUpdate ? query.ForUpdate().ToArray() : query.ToArray();
            actual.Select(d => d["_id"].AsInt32).OrderBy(x => x).Should().Equal(Enumerable.Range(1, 72));
            BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(forUpdate ? 72 : 32);
        }

        internal static BsonDocument[] Documents() => Enumerable.Range(1, 72).Select(i => new BsonDocument
        {
            ["_id"] = i, ["Score"] = i, ["Name"] = i <= 32 || i % 2 == 0 ? "London" : "LONDON",
            ["Nested"] = new BsonDocument { ["Value"] = i <= 32 ? i : -i },
            ["Ref"] = new BsonDocument { ["$id"] = 1, ["$ref"] = "owners" },
            ["Payload"] = new string((char)('a' + i % 20), 1024)
        }).ToArray();
    }
}
