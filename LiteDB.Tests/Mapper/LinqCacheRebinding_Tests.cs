using System;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class LinqCacheRebinding_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void String_comparison_mode_changes_match_fresh_translation(bool staticCall, bool ordinalFirst)
        {
            var mapper = new BsonMapper();
            var mode = ordinalFirst ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            Expression<Func<Row, bool>> query = staticCall
                ? x => string.Equals(x.Name, "READY", mode)
                : x => x.Name.Equals("READY", mode);
            var document = new BsonDocument { ["Name"] = "ready" };
            for (var i = 0; i < 4; i++)
            {
                var actual = mapper.GetExpression(query);
                var fresh = new BsonMapper().GetExpression(query);
                actual.Source.Should().Be(fresh.Source);
                actual.ExecuteScalar(document, Collation.Binary).AsBoolean.Should().Be(query.Compile()(new Row { Name = "ready" }));
                mode = mode == StringComparison.Ordinal ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Enum_equals_rebinds_null_matching_and_nonmatching_runtime_types(bool integer)
        {
            var mapper = new BsonMapper { EnumAsInteger = integer };
            var document = new BsonDocument { ["State"] = integer ? new BsonValue(1) : new BsonValue("Ready") };
            object captured = State.Ready;
            Expression<Func<Row, bool>> query = x => x.State.Equals(captured);
            foreach (var value in new object[] { State.Ready, null, "Ready", 1, OtherState.Ready, State.New, State.Ready })
            {
                captured = value;
                var actual = mapper.GetExpression(query);
                actual.ExecuteScalar(document).AsBoolean.Should().Be(State.Ready.Equals(value));
                actual.Source.Should().Be(new BsonMapper { EnumAsInteger = integer }.GetExpression(query).Source);
            }
        }

        [Fact]
        public void Member_selection_is_revalidated_when_mapping_precedence_changes()
        {
            var mapper = new BsonMapper();
            Expression<Func<Row, string>> query = x => x.Name;
            mapper.GetExpression(query).Source.Should().Be("$.Name");
            var members = mapper.GetEntityMapper(typeof(Row)).Members;
            var replacement = new MemberMapper
            {
                MemberName = nameof(Row.Name), FieldName = "replacement", DataType = typeof(string)
            };
            members.Insert(0, replacement);
            mapper.GetExpression(query).Source.Should().Be("$.replacement");
            members.Remove(replacement);
            mapper.GetExpression(query).Source.Should().Be("$.Name");
        }

        [Fact]
        public void Bson_values_bypass_custom_mapper_serialization_on_cold_and_hot_paths()
        {
            var mapper = new TrackingMapper();
            BsonValue captured = 7;
            Expression<Func<Row, BsonValue>> query = x => captured;
            for (var i = 0; i < 3; i++)
            {
                mapper.GetExpression(query).ExecuteScalar(new BsonDocument()).Should().Be(captured);
                captured = new BsonDocument { ["value"] = i };
            }
            mapper.BsonSerializations.Should().Be(0);
        }

        [Fact]
        public void Captured_dbrefs_are_reread_after_null_and_value_changes()
        {
            var mapper = new BsonMapper();
            Row captured = null;
            Expression<Func<Row, Reference>> query = x => new Reference { Item = captured, Items = new[] { captured } };
            foreach (var id in new[] { 0, 1, 2, 0, 3 })
            {
                captured = id == 0 ? null : new Row { Id = id };
                var actual = mapper.GetExpression(query).ExecuteScalar(new BsonDocument());
                var fresh = new BsonMapper().GetExpression(query).ExecuteScalar(new BsonDocument());
                actual.Should().Be(fresh);
                actual["Items"].AsArray.Count.Should().Be(captured == null ? 0 : 1);
                if (captured != null) actual["Item"]["$id"].AsInt32.Should().Be(id);
            }
        }

        [Fact]
        public void Capture_failures_have_the_same_exception_type_on_cold_and_warm_paths()
        {
            var mapper = new BsonMapper();
            var values = new System.Collections.Generic.List<int> { 1 };
            Expression<Func<Row, bool>> query = x => x.Id == values.First();
            mapper.GetExpression(query);
            mapper.GetExpression(query);
            values.Clear();
            Action warm = () => mapper.GetExpression(query);
            Action cold = () => new BsonMapper().GetExpression(query);
            var expected = cold.Should().Throw<Exception>().Which;
            warm.Should().Throw<Exception>().Which.GetType().Should().Be(expected.GetType());
        }

        private sealed class TrackingMapper : BsonMapper
        {
            internal int BsonSerializations;
            public override BsonValue Serialize(Type type, object value, int depth)
            {
                if (value is BsonValue) BsonSerializations++;
                return base.Serialize(type, value, depth);
            }
        }

        public enum State { New, Ready }
        public enum OtherState { New, Ready }
        public class Row
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public State State { get; set; }
        }
        public class Reference
        {
            [BsonRef("rows")]
            public Row Item { get; set; }
            [BsonRef("rows")]
            public Row[] Items { get; set; }
        }
    }
}
