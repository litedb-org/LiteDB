using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    public class ExpressionCacheControl_Tests
    {
        [Fact]
        public void Production_cache_switch_bypasses_expression_linq_and_sql_caches()
        {
            var previous = BsonExpression.CacheEnabled;
            try
            {
                BsonExpression.CacheEnabled = false;
                var compiledBefore = BsonExpression.CompiledExpressionCount;
                for (var i = 0; i < 3; i++)
                {
                    var expression = BsonExpression.Create("Value = @value",
                        new BsonDocument { ["value"] = i });
                    expression.ExecuteScalar(new BsonDocument { ["Value"] = i }).AsBoolean.Should().BeTrue();
                }
                BsonExpression.CompiledExpressionCount.Should().Be(compiledBefore);

                var mapper = new BsonMapper();
                for (var i = 0; i < 3; i++)
                {
                    var value = i;
                    Expression<Func<CacheRow, bool>> predicate = row => row.Value == value;
                    mapper.GetExpression(predicate).ExecuteScalar(
                        new BsonDocument { ["Value"] = i }).AsBoolean.Should().BeTrue();
                }
                mapper.LinqExpressionCacheCount.Should().Be(0);

                using var db = new LiteDatabase(":memory:");
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                for (var i = 0; i < 3; i++)
                {
                    using var reader = db.Execute("SELECT $ FROM rows WHERE _id = @id",
                        new BsonDocument { ["id"] = 1 });
                    reader.Read().Should().BeTrue();
                }
                typeof(LiteDatabase).GetField("_sqlQueryCache", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(db).Should().BeNull();
            }
            finally
            {
                BsonExpression.CacheEnabled = previous;
            }
        }

        [Fact]
        public void BindCore_field_inventory_requires_an_explicit_copy_or_exclusion()
        {
            var copied = new[]
            {
                "Expression", "Fields", "GroupKeyAliases", "IsANY", "IsImmutable", "IsScalar",
                "IsVolatile", "Left", "Parameters", "RequiresExactSort", "Right", "Source", "Type",
                "UseSource", "_funcEnumerable", "_funcScalar"
            };
            var parseOnly = new[] { "SelectAliases", "SelectContext", "_selectAliasCompiled" };
            var expected = copied.Concat(parseOnly).OrderBy(x => x).ToArray();
            var actual = typeof(BsonExpression)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(field => Normalize(field.Name))
                .OrderBy(x => x)
                .ToArray();

            actual.Should().Equal(expected,
                "every new BsonExpression field must be copied by BindCore or listed as parse-only here");
        }

        private static string Normalize(string name)
        {
            if (!name.StartsWith("<", StringComparison.Ordinal)) return name;
            return name.Substring(1, name.IndexOf('>') - 1);
        }

        private sealed class CacheRow
        {
            public int Value { get; set; }
        }
    }
}
