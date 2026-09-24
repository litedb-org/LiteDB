using System;
using System.Collections.Generic;
using System.Linq;

using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class BorrowedQuery_Tests
    {
        [Fact]
        public void Selective_filter_materializes_only_limited_survivors()
        {
            using var db = CreateLegacyDatabase();
            var collection = db.GetCollection("items", BsonAutoId.Int32);

            collection.InsertBulk(Enumerable.Range(0, 100).Select(index => new BsonDocument
            {
                ["Score"] = index,
                ["Payload"] = new string('x', 1024)
            }));

            BorrowedQueryDiagnostics.Reset();

            var results = collection.Query()
                .Where("$.Score >= 90")
                .Limit(3)
                .ToList();

            results.Select(x => x["Score"].AsInt32).Should().Equal(90, 91, 92);
            BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(3);
            BorrowedQueryDiagnostics.DocumentsExamined.Should().Be(93);
            BorrowedQueryDiagnostics.BorrowedPredicateFallbacks.Should().Be(0);
        }

        [Fact]
        public void Count_evaluates_multiple_nested_predicates_without_materialization()
        {
            using var db = CreateLegacyDatabase();
            var collection = db.GetCollection("items", BsonAutoId.Int32);

            collection.InsertBulk(Enumerable.Range(0, 100).Select(index => new BsonDocument
            {
                ["Score"] = index,
                ["Profile"] = new BsonDocument { ["City"] = index % 2 == 0 ? "London" : "Paris" }
            }));

            var parameters = new BsonDocument { ["city"] = "LONDON", ["minimum"] = 80 };
            BorrowedQueryDiagnostics.Reset();

            var count = collection.Count(BsonExpression.Create(
                "$.Profile.City = @city AND $.Score >= @minimum", parameters));

            count.Should().Be(10);
            BorrowedQueryDiagnostics.DocumentsExamined.Should().Be(100);
            BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(0);
            BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(100);
        }

        [Fact]
        public void Exists_stops_at_first_borrowed_match()
        {
            using var db = CreateLegacyDatabase();
            var collection = db.GetCollection("items", BsonAutoId.Int32);

            collection.InsertBulk(Enumerable.Range(0, 100).Select(index => new BsonDocument
            {
                ["Score"] = index,
                ["Payload"] = new string('x', 512)
            }));

            BorrowedQueryDiagnostics.Reset();

            collection.Exists("$.Score = 0").Should().BeTrue();
            BorrowedQueryDiagnostics.DocumentsExamined.Should().Be(1);
            BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(0);
        }

        [Fact]
        public void Sql_count_uses_the_same_borrowed_plan()
        {
            using var db = CreateLegacyDatabase();
            var collection = db.GetCollection("items", BsonAutoId.Int32);

            collection.InsertBulk(Enumerable.Range(0, 50).Select(index => new BsonDocument
            {
                ["Score"] = index,
                ["Payload"] = new string('x', 512)
            }));

            BorrowedQueryDiagnostics.Reset();

            var result = db.Execute(
                "SELECT { count: COUNT(*._id) } FROM items WHERE $.Score >= 40")
                .Single();

            result["count"].AsInt32.Should().Be(10);
            BorrowedQueryDiagnostics.DocumentsExamined.Should().Be(50);
            BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(0);
        }

        [Fact]
        public void Sql_count_with_computed_input_uses_materialized_fallback()
        {
            using var db = CreateLegacyDatabase();
            var collection = db.GetCollection("items", BsonAutoId.Int32);

            collection.InsertBulk(Enumerable.Range(0, 10).Select(index => new BsonDocument
            {
                ["Score"] = index
            }));

            BorrowedQueryDiagnostics.Reset();

            var result = db.Execute("SELECT { count: COUNT(MAP(* => @.Score + 1)) } FROM items")
                .Single();

            result["count"].AsInt32.Should().Be(10);
            BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(10);
        }

        [Fact]
        public void Multi_page_documents_are_filtered_without_materialization()
        {
            using var db = CreateLegacyDatabase();
            var collection = db.GetCollection("items", BsonAutoId.Int32);

            collection.InsertBulk(Enumerable.Range(0, 8).Select(index => new BsonDocument
            {
                ["Payload"] = new string((char)('a' + index), 20_000),
                ["Nested"] = new BsonDocument { ["Score"] = index }
            }));

            BorrowedQueryDiagnostics.Reset();

            collection.Count("$.Nested.Score >= 5").Should().Be(3);
            BorrowedQueryDiagnostics.DocumentsExamined.Should().Be(8);
            BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(0);
        }

        [Fact]
        public void Scalar_sort_materializes_only_the_final_window()
        {
            using var db = CreateLegacyDatabase();
            var collection = db.GetCollection("items", BsonAutoId.Int32);

            collection.InsertBulk(Enumerable.Range(0, 100).Select(index => new BsonDocument
            {
                ["Score"] = 100 - index,
                ["Payload"] = new string('x', 1024)
            }));

            BorrowedQueryDiagnostics.Reset();

            var results = collection.Query()
                .OrderBy("$.Score")
                .Limit(5)
                .ToList();

            results.Select(x => x["Score"].AsInt32).Should().Equal(1, 2, 3, 4, 5);
            BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(5);
        }

        [Fact]
        public void Direct_projection_builds_only_the_result_document()
        {
            using var db = CreateLegacyDatabase();
            var collection = db.GetCollection("items", BsonAutoId.Int32);

            collection.InsertBulk(Enumerable.Range(0, 100).Select(index => new BsonDocument
            {
                ["Name"] = "item-" + index,
                ["Score"] = index,
                ["Payload"] = new string('x', 1024)
            }));

            BorrowedQueryDiagnostics.Reset();

            var results = collection.Query()
                .Where("$.Score >= 90")
                .Select("{ Name: $.Name, Score: $.Score }")
                .ToList();

            results.Should().HaveCount(10);
            results[0]["Name"].AsString.Should().Be("item-90");
            results[0].Count.Should().Be(2);
            BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(0);
        }

        [Fact]
        public void Borrowed_numeric_comparisons_match_owning_expression_semantics()
        {
            using var db = CreateLegacyDatabase();
            var collection = db.GetCollection("items", BsonAutoId.Int32);
            var documents = new List<BsonDocument>
            {
                new BsonDocument { ["Value"] = 1 },
                new BsonDocument { ["Value"] = 1L },
                new BsonDocument { ["Value"] = 1.0 },
                new BsonDocument { ["Value"] = 1.0m },
                new BsonDocument { ["Value"] = 2 },
                new BsonDocument { ["Value"] = BsonValue.Null },
                new BsonDocument()
            };

            collection.InsertBulk(documents);

            foreach (var source in new[] { "$.Value = 1", "$.Value != 1", "$.Value >= 1.0" })
            {
                var expression = BsonExpression.Create(source);
                var expected = documents.Count(document => expression.ExecuteScalar(document).AsBoolean);

                BorrowedQueryDiagnostics.Reset();
                collection.Count(expression).Should().Be(expected, source);
                BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(0, source);
            }
        }

        [Fact]
        public void Unsupported_unicode_paths_use_materialized_fallback()
        {
            using var db = CreateLegacyDatabase();
            var collection = db.GetCollection("items", BsonAutoId.Int32);

            collection.InsertBulk(Enumerable.Range(0, 10).Select(index => new BsonDocument
            {
                ["Straße"] = index
            }));

            BorrowedQueryDiagnostics.Reset();

            collection.Count("$.Straße >= 5").Should().Be(5);
            BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(0);
            BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(10);
        }

        [Fact]
        public void Non_ascii_stored_names_preserve_ordinal_ignore_case_semantics()
        {
            using var db = CreateLegacyDatabase();
            var collection = db.GetCollection("items", BsonAutoId.Int32);
            var document = new BsonDocument { ["K"] = 5 };
            var expression = BsonExpression.Create("$.K = 5");
            var expected = expression.ExecuteScalar(document).AsBoolean ? 1 : 0;

            collection.Insert(document);
            BorrowedQueryDiagnostics.Reset();

            collection.Count(expression).Should().Be(expected);
            BorrowedQueryDiagnostics.BorrowedPredicateFallbacks.Should().Be(1);
            BorrowedQueryDiagnostics.DocumentsMaterialized.Should().Be(1);
        }

        [Fact]
        public void Borrowed_slot_buffer_round_trips_values_from_the_byte_pool()
        {
            using var buffer = new BorrowedValueBuffer(4);
            var guid = Guid.NewGuid();

            buffer[0] = BorrowedBsonValue.FromInt64(long.MaxValue);
            buffer[1] = BorrowedBsonValue.FromDecimal(123.456m);
            buffer[2] = BorrowedBsonValue.FromGuid(guid);
            buffer[3] = BorrowedBsonValue.FromString("borrowed");

            buffer[0].TryMaterialize(out var integer).Should().BeTrue();
            buffer[1].TryMaterialize(out var number).Should().BeTrue();
            buffer[2].TryMaterialize(out var identifier).Should().BeTrue();
            buffer[3].TryMaterialize(out var text).Should().BeTrue();
            integer.AsInt64.Should().Be(long.MaxValue);
            number.AsDecimal.Should().Be(123.456m);
            identifier.AsGuid.Should().Be(guid);
            text.AsString.Should().Be("borrowed");

            buffer.Reset(4);
            buffer[3].Type.Should().Be(BsonType.Null);
        }

        [Fact]
        public void Borrowed_row_aggregate_projects_counts_beyond_int32()
        {
            var aggregate = RowAggregate.TryCreate(QueryAggregateExpressions.Count);

            aggregate.Should().NotBeNull();
            aggregate.Project((long)int.MaxValue + 1)["count"].AsInt64
                .Should().Be((long)int.MaxValue + 1);
        }

        private static LiteDatabase CreateLegacyDatabase() =>
            DatabaseFactory.Create(connectionString: "Filename=:memory:;Compact Storage=Legacy");
    }
}
