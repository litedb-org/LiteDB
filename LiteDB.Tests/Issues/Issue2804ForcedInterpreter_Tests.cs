using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2804ForcedInterpreter_Tests
    {
        [Fact]
        public void Startup_switch_initializers_cannot_run_before_first_use()
        {
            (typeof(RuntimeExpression).Attributes & TypeAttributes.BeforeFieldInit).Should().Be((TypeAttributes)0);
            (typeof(BsonExpression).Attributes & TypeAttributes.BeforeFieldInit).Should().Be((TypeAttributes)0);
        }

        public struct Coordinates { public int X { get; set; } public int Y { get; set; } }
        public class Record
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public Coordinates Point { get; set; }
            public string[] Values { get; set; }
            public int? Optional { get; private set; }
            public void SetOptional(int? value) => Optional = value;
        }

        [Fact]
        public void Forced_AOT_path_supports_mapping_queries_nested_aliases_and_indexes()
        {
            RuntimeExpression.ForceInterpretation = true;
            try
            {
                var mapper = new BsonMapper();
                var input = new Record { Id = 7, Name = "Ada", Point = new Coordinates { X = 3, Y = 4 }, Values = new[] { "one", "two" } };
                input.SetOptional(42);
                var copy = mapper.ToObject<Record>(mapper.ToDocument(input));
                copy.Optional.Should().Be(42);
                input.SetOptional(null);
                mapper.ToObject<Record>(mapper.ToDocument(input)).Optional.Should().BeNull();
                copy.Id.Should().Be(7);
                copy.Point.X.Should().Be(3);
                copy.Point.Y.Should().Be(4);
                copy.Values.Should().Equal("one", "two");
                using var db = new LiteDatabase(":memory:", mapper);
                var collection = db.GetCollection<Record>("records");
                collection.Insert(new[] { input, new Record { Id = 2, Name = "Grace", Values = new[] { "three" } } });
                var minimum = 5;
                collection.Find(row => row.Id >= minimum && row.Name == "Ada").Select(row => row.Id).Should().Equal(7);
                collection.EnsureIndex(row => row.Name);
                collection.Find(row => row.Name == "Ada").Select(row => row.Id).Should().Equal(7);
                using var result = db.Execute("SELECT _id, ARRAY(MAP(Values[*] => UPPER(@))) AS aot_alias FROM records ORDER BY aot_alias");
                result.ToArray().Select(row => row["_id"].AsInt32).Should().Equal(7, 2);
                collection.Update(new Record { Id = 7, Name = "Updated" }).Should().BeTrue();
                collection.FindById(7).Name.Should().Be("Updated");
            }
            finally { RuntimeExpression.ForceInterpretation = false; }
        }

        [Fact]
        public void Forced_AOT_preserves_established_captured_CLR_forms()
        {
            RuntimeExpression.ForceInterpretation = true;
            try
            {
                var existing = new Issue1908_Tests();
                existing.Captured_element_conversions_keep_CLR_values();
                existing.Captured_array_lengths_keep_CLR_null_behavior_and_runtime_arrays_keep_LENGTH();
                existing.Explicit_IndexExpression_reads_current_captured_value();
                existing.Closed_scalar_branches_do_not_read_unchosen_elements();
                existing.Captured_scalar_list_and_dictionary_values_preserve_row_index_queries();
                new Issue1829Invocation_Tests().Invocation_inside_a_captured_index_keeps_single_client_evaluation();
                new Issue2739_Tests().Captured_reference_lists_match_insert_serialization(true);
            }
            finally { RuntimeExpression.ForceInterpretation = false; }
        }

        [Fact]
        public void Captured_selector_lambdas_require_precomputation_on_AOT()
        {
            var values = new[] { new Record { Id = 7 } };
            RuntimeExpression.ForceInterpretation = true;
            try
            {
                var mapper = new BsonMapper();
                Action inline = () => mapper.GetExpression<Record, bool>(row => row.Id == values.First(value => value.Id > 0).Id);
                inline.Should().Throw<TargetInvocationException>().Which.InnerException.Should().BeOfType<NotSupportedException>();
                var cutoff = values.First(value => value.Id > 0).Id;
                var predicate = mapper.GetExpression<Record, bool>(row => row.Id == cutoff);
                predicate.ExecuteScalar(new BsonDocument { ["_id"] = 7 }).AsBoolean.Should().BeTrue();
                predicate.ExecuteScalar(new BsonDocument { ["_id"] = 8 }).AsBoolean.Should().BeFalse();
            }
            finally { RuntimeExpression.ForceInterpretation = false; }
        }

        [Fact]
        public void Forced_compilation_bypasses_and_does_not_pollute_the_JIT_cache()
        {
            var field = typeof(BsonExpression).GetField("_funcScalar", BindingFlags.Instance | BindingFlags.NonPublic);
            const string source = "$.aot_cache_mode_test + 4";
            var original = field.GetValue(BsonExpression.Create(source));
            RuntimeExpression.ForceInterpretation = true;
            object forced;
            try { forced = field.GetValue(BsonExpression.Create(source)); }
            finally { RuntimeExpression.ForceInterpretation = false; }
            forced.Should().NotBeSameAs(original);
            field.GetValue(BsonExpression.Create(source)).Should().BeSameAs(original);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Captured_evaluation_preserves_invocation_exception_wrapping(bool interpret)
        {
            RuntimeExpression.ForceInterpretation = interpret;
            try
            {
                var array = new int[0];
                Action translate = () => new BsonMapper().GetExpression<Record, int>(row => array[0]);
                translate.Should().Throw<TargetInvocationException>().Which.InnerException.Should().BeOfType<IndexOutOfRangeException>();
            }
            finally { RuntimeExpression.ForceInterpretation = false; }
        }

        [Fact]
        public void Forced_AOT_short_circuit_does_not_evaluate_unselected_branch()
        {
            RuntimeExpression.ForceInterpretation = true;
            try
            {
                var document = new BsonDocument { ["aot_guard"] = true };
                BsonExpression.Create("$.aot_guard OR SUBSTRING('x', 999) = 'bad'").ExecuteScalar(document).AsBoolean.Should().BeTrue();
                BsonExpression.Create("IIF($.aot_guard, 42, SUBSTRING('x', 999))").ExecuteScalar(document).AsInt32.Should().Be(42);
            }
            finally { RuntimeExpression.ForceInterpretation = false; }
        }
    }
}
