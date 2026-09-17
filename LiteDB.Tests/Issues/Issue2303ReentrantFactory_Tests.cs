using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2303ReentrantFactory_Tests
    {
        public class Row
        {
            private int _value;
            public Row(int value) { _value = value; }
            public int Value { get => _value; set { _value = value; Sets++; } }
            [BsonIgnore] public int Sets { get; private set; }
        }

        private sealed class State { public Row Current; public bool Nested; }
        private sealed class Mapper : BsonMapper
        {
            private readonly State _state;
            public Mapper(State state) : base(type => state.Nested && type == typeof(Row) ? state.Current : null)
            {
                _state = state;
            }
            protected override void DeserializeObject(Type type, object instance, BsonDocument document)
            {
                if (!_state.Nested && instance is Row row)
                {
                    _state.Current = row;
                    _state.Nested = true;
                    try { Assert.Same(row, ToObject<Row>(new BsonDocument { ["Value"] = 22 })); }
                    finally { _state.Nested = false; }
                }
                base.DeserializeObject(type, instance, document);
            }
        }

        [Fact]
        public void Reentrant_instantiator_population_does_not_share_outer_constructor_suppression()
        {
            var mapper = new Mapper(new State());
            var row = mapper.ToObject<Row>(new BsonDocument { ["Value"] = 11 });
            row.Value.Should().Be(22);
            row.Sets.Should().Be(1);
        }
    }
}
