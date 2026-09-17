using System;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using LiteDB.Tests.Mapper;
using Xunit;
using static LiteDB.Tests.QueryTest.SqlQueryTemplate_Tests;

namespace LiteDB.Tests.QueryTest
{
    public class SqlQueryCache_Tests
    {
        [Fact]
        public void First_use_keeps_only_a_key_until_a_repeated_statement_is_promoted()
        {
            var cache = new SqlQueryCache();
            const string sql = "SELECT $ FROM rows";
            cache.TryGet(sql, out _, out var repeated).Should().BeFalse();
            repeated.Should().BeFalse();
            cache.Add(sql, null);
            cache.TryGet(sql, out var missing, out repeated).Should().BeFalse();
            repeated.Should().BeTrue();
            missing.Should().BeNull();
            var template = new SqlQueryTemplate("rows", new Query());
            cache.Add(sql, template);
            cache.Add(sql, null); // A concurrent first-use completion cannot demote it.
            cache.TryGet(sql, out var retained, out _).Should().BeTrue();
            retained.Should().BeSameAs(template);
        }

        [Fact]
        public void Cache_is_bounded_and_retains_recently_used_statements()
        {
            var cache = new SqlQueryCache();
            var template = new SqlQueryTemplate("rows", new Query());
            for (var i = 0; i < SqlQueryCache.Capacity; i++) cache.Add("SELECT " + i, template);
            cache.TryGet("SELECT 0", out _, out _).Should().BeTrue();
            cache.Add("SELECT overflow", template);
            cache.TryGet("SELECT 0", out _, out _).Should().BeTrue();
            cache.TryGet("SELECT 1", out _, out _).Should().BeFalse();
            var oversized = new string('x', SqlQueryCache.MaximumCommandLength + 1);
            cache.Add(oversized, template);
            cache.TryGet(oversized, out _, out _).Should().BeFalse();
        }

        [Fact]
        public void Cache_does_not_retain_the_first_callers_parameter_payload()
        {
            using var db = CreateDatabase();
            var references = Populate(db);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            references.Should().OnlyContain(x => !x.IsAlive);
            using var scope = new DirectTranslationScope();
            Read(db, "SELECT { values: ARRAY(MAP(@values => @ + @delta)) }",
                new BsonDocument { ["values"] = new BsonArray(1), ["delta"] = 2 })
                .Single()["values"].AsArray.ToArray().Should().Equal(new BsonValue[] { 3 });
        }

        [Fact]
        public void Oversized_statements_and_parse_errors_keep_the_regular_parser_path()
        {
            using var db = CreateDatabase();
            var sql = "SELECT '" + new string('a', SqlQueryCache.MaximumCommandLength) + "'";
            Read(db, sql).Single()["expr"].AsString.Length.Should().Be(SqlQueryCache.MaximumCommandLength);
            using (var scope = new DirectTranslationScope())
            {
                Action execute = () => Read(db, sql);
                execute.Should().Throw<InvalidOperationException>();
            }
            for (var i = 0; i < 2; i++)
            {
                Action invalid = () => Read(db, "SELECT $ FROM rows WHERE (");
                invalid.Should().Throw<LiteException>();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] Populate(LiteDatabase db)
        {
            var values = new BsonArray(Enumerable.Range(1, 1000).Select(i => new BsonValue(i)));
            var parameters = new BsonDocument { ["values"] = values, ["delta"] = 5, ["payload"] = new byte[1024 * 1024] };
            Read(db, "SELECT { values: ARRAY(MAP(@values => @ + @delta)) }", parameters);
            Read(db, "SELECT { values: ARRAY(MAP(@values => @ + @delta)) }", parameters);
            return new[] { new WeakReference(parameters), new WeakReference(values), new WeakReference(parameters["payload"].AsBinary) };
        }
    }
}
