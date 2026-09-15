using System;
using System.Globalization;
using System.IO;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Audit2026
{
    public class SqlAuditRegression_Tests
    {
        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M104_where_precedes_order_limit_offset_and_for_update_in_to_sql()
        {
            var query = new Query { Limit = 5, Offset = 2, ForUpdate = true };
            query.Where.Add(BsonExpression.Create("$.active = true"));
            query.OrderBy.Add(new QueryOrder(BsonExpression.Create("$.name"), Query.Ascending));

            var sql = query.ToSQL("people");
            sql.IndexOf("WHERE", StringComparison.Ordinal).Should()
                .BeLessThan(sql.IndexOf("ORDER BY", StringComparison.Ordinal));
            sql.IndexOf("WHERE", StringComparison.Ordinal).Should()
                .BeLessThan(sql.IndexOf("LIMIT", StringComparison.Ordinal));
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M142_trailing_statement_is_rejected_instead_of_silently_discarded()
        {
            using var db = new LiteDatabase(new MemoryStream());

            Action execute = () => db.Execute(
                "INSERT INTO items:INT VALUES {value:1}; INSERT INTO items:INT VALUES {value:2}").Dispose();

            execute.Should().Throw<LiteException>();
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M143_keyword_dispatch_is_invariant_under_turkish_culture()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                using var db = new LiteDatabase(new MemoryStream());

                Action execute = () => db.Execute("insert into items:int values {value:1}").Dispose();
                execute.Should().NotThrow();
                db.GetCollection("items").Count().Should().Be(1);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M144_transaction_commands_reject_unknown_trailing_clauses()
        {
            using var db = new LiteDatabase(new MemoryStream());
            db.BeginTrans().Should().BeTrue();

            Action rollback = () => db.Execute("ROLLBACK garbage").Dispose();
            rollback.Should().Throw<LiteException>();
            db.Rollback().Should().BeTrue();
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void L192_large_limit_is_reported_as_sql_parse_error()
        {
            using var db = new LiteDatabase(new MemoryStream());

            Action execute = () => db.Execute("SELECT $ FROM items LIMIT 2147483648").Dispose();
            execute.Should().Throw<LiteException>();
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void L195_vector_to_sql_is_invariant_and_does_not_duplicate_predicate()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var query = new Query
                {
                    VectorField = "Embedding",
                    VectorTarget = new[] { 1.5f, 0.25f },
                    VectorMaxDistance = 0.75
                };
                query.Where.Add(BsonExpression.Create(
                    "$.Embedding VECTOR_SIM [1.5, 0.25] <= 0.75"));

                var sql = query.ToSQL("vectors");
                sql.Should().Contain("[1.5,0.25]");
                Count(sql, "VECTOR_SIM").Should().Be(1);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        private static int Count(string value, string term)
        {
            var count = 0;
            var offset = 0;
            while ((offset = value.IndexOf(term, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += term.Length;
            }
            return count;
        }
    }
}
