using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2303ConstructorMapping_Tests
    {
        public class Renamed
        {
            private int _value;
            public Renamed(int value) { _value = value + 1; }
            [BsonField("stored")]
            public int Value { get => _value; set { _value = value; Sets++; } }
            public string Other { get; set; }
            [BsonIgnore] public int Sets { get; private set; }
        }

        public class Reference
        {
            public int Id { get; set; }
        }

        public class WithReference
        {
            private Reference _reference;
            [BsonCtor]
            public WithReference(Reference reference) { _reference = reference; }
            [BsonRef("targets")]
            public Reference Reference { get => _reference; set { _reference = value; Sets++; } }
            [BsonIgnore] public int Sets { get; private set; }
        }

        public class Choice
        {
            private int _value;
            public Choice() { }
            public Choice(int value) { _value = value + 1; }
            public int Value { get => _value; set { _value = value; Sets++; } }
            [BsonIgnore] public int Sets { get; private set; }
        }

        private sealed class PopulationMapper : BsonMapper
        {
            public int Calls { get; private set; }
            public BsonDocument LastDocument { get; private set; }
            protected override void DeserializeObject(Type type, object obj, BsonDocument value)
            {
                Calls++;
                LastDocument = value;
                base.DeserializeObject(type, obj, value);
            }
        }

#if !NETFRAMEWORK
        [Fact]
        public void Null_custom_index_factory_retains_builtin_fallback()
        {
            var mapper = new BsonMapper();
            mapper.GetEntityMapper(typeof(Index)).CreateInstance = _ => null;
            var value = mapper.Serialize(typeof(Index), Index.FromEnd(3));
            mapper.Deserialize<Index>(value).Should().Be(Index.FromEnd(3));
        }
#endif

        private sealed class DecoratingMapper : BsonMapper
        {
            protected override CreateObject GetTypeCtor(EntityMapper entity)
            {
                var factory = base.GetTypeCtor(entity);
                return factory == null ? null : new CreateObject(document => factory(document));
            }
        }

        [Fact]
        public void Decorated_constructor_factory_keeps_bound_member_suppression()
        {
            var mapper = new DecoratingMapper();
            var value = mapper.ToObject<Renamed>(new BsonDocument { ["stored"] = 8, ["Other"] = "unbound" });
            value.Value.Should().Be(9);
            value.Sets.Should().Be(0);
            value.Other.Should().Be("unbound");
        }

        [Fact]
        public void Explicit_null_factory_retains_empty_document_behavior()
        {
            var mapper = new BsonMapper();
            mapper.Entity<Renamed>().Ctor(_ => null);
            Assert.Null(mapper.ToObject<Renamed>(new BsonDocument()));
        }

        [Fact]
        public void Missing_constructor_field_does_not_invoke_member_decoder()
        {
            var mapper = new BsonMapper();
            mapper.ResolveMember = (type, member, mapped) =>
            {
                if (type == typeof(Renamed) && member.Name == "Value")
                    mapped.Deserialize = (value, _) => throw new InvalidOperationException("Unexpected decoder");
            };
            mapper.ToObject<Renamed>(new BsonDocument()).Value.Should().Be(1);
        }

        [Fact]
        public void Inferred_constructor_uses_renamed_fields_and_preserves_input_and_hook()
        {
            var mapper = new PopulationMapper();
            var input = new BsonDocument { ["stored"] = 8, ["Other"] = "remaining" };
            for (var i = 0; i < 2; i++)
            {
                var result = mapper.ToObject<Renamed>(input);
                result.Value.Should().Be(9);
                Assert.Same(input, mapper.LastDocument);
                mapper.LastDocument["stored"].AsInt32.Should().Be(8);
                result.Sets.Should().Be(0);
                result.Other.Should().Be("remaining");
                input["stored"].AsInt32.Should().Be(8);
                input.Count.Should().Be(2);
            }
            mapper.Calls.Should().Be(2);
        }

        [Fact]
        public void Constructor_parameters_use_reference_member_decoder()
        {
            var result = new BsonMapper().ToObject<WithReference>(new BsonDocument
            {
                ["Reference"] = new BsonDocument { ["$id"] = 42, ["$ref"] = "targets" }
            });
            result.Reference.Id.Should().Be(42);
            result.Sets.Should().Be(0);
        }

        [Fact]
        public void Parameterless_and_explicit_factories_still_populate_members()
        {
            var mapper = new BsonMapper();
            var choice = mapper.ToObject<Choice>(new BsonDocument { ["Value"] = 8 });
            choice.Value.Should().Be(8);
            choice.Sets.Should().Be(1);

            var input = new BsonDocument { ["stored"] = 8 };
            mapper.ToObject<Renamed>(input).Sets.Should().Be(0);
            mapper.Entity<Renamed>().Ctor(_ => new Renamed(100));
            var custom = mapper.ToObject<Renamed>(input);
            custom.Value.Should().Be(8);
            custom.Sets.Should().Be(1);
        }

        [Fact]
        public void Type_instantiator_is_not_mistaken_for_cached_parameter_constructor()
        {
            var useCustom = false;
            var mapper = new BsonMapper(type => useCustom && type == typeof(Renamed) ? new Renamed(100) : null);
            var input = new BsonDocument { ["stored"] = 8 };
            mapper.ToObject<Renamed>(input).Sets.Should().Be(0);
            useCustom = true;
            var result = mapper.ToObject<Renamed>(input);
            result.Value.Should().Be(8);
            result.Sets.Should().Be(1);
        }
    }
}
