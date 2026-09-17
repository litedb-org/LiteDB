using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2303ConstructorCleanup_Tests
    {
        public class Bag : Dictionary<string, int>
        {
            private int _seed;
            public Bag(int seed) { _seed = seed; }
            public int Seed { get => _seed; set { _seed = value; Sets++; } }
            [BsonIgnore] public int Sets { get; private set; }
        }

        private sealed class Mapper : BsonMapper
        {
            public object Constructed { get; private set; }
            public bool ThrowAfterConstruction { get; set; }
            protected override CreateObject GetTypeCtor(EntityMapper entity)
            {
                var factory = base.GetTypeCtor(entity);
                return factory == null ? null : new CreateObject(document =>
                {
                    Constructed = factory(document);
                    if (ThrowAfterConstruction) throw new InvalidOperationException("decorator failure");
                    return Constructed;
                });
            }
            public void Populate(Bag bag, BsonDocument value) => base.DeserializeObject(typeof(Bag), bag, value);
        }

        [Fact]
        public void Explicit_factory_can_replace_itself_for_subsequent_deserializations()
        {
            var mapper = new BsonMapper();
            mapper.Entity<Bag>().Ctor(_ =>
            {
                mapper.Entity<Bag>().Ctor(__ => new Bag(22));
                return new Bag(11);
            });
            mapper.ToObject<Bag>(new BsonDocument()).Seed.Should().Be(11);
            mapper.ToObject<Bag>(new BsonDocument()).Seed.Should().Be(22);
        }

        [Fact]
        public void Failing_factory_decorator_clears_metadata_on_its_retained_instance()
        {
            var mapper = new Mapper { ThrowAfterConstruction = true };
            Assert.Throws<InvalidOperationException>(() => mapper.ToObject<Bag>(new BsonDocument { ["Seed"] = 8 }));
            var bag = Assert.IsType<Bag>(mapper.Constructed);
            mapper.Populate(bag, new BsonDocument { ["Seed"] = 6 });
            bag.Seed.Should().Be(6);
            bag.Sets.Should().Be(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Dictionary_constructor_metadata_is_cleared_after_population(bool failPopulation)
        {
            var mapper = new Mapper();
            var input = new BsonDocument { ["Seed"] = 8 };
            if (failPopulation) input["broken"] = "not an integer";
            var failure = Record.Exception(() => mapper.ToObject<Bag>(input));
            if (failPopulation) Assert.NotNull(failure);
            else Assert.Null(failure);
            var bag = Assert.IsType<Bag>(mapper.Constructed);
            mapper.Populate(bag, new BsonDocument { ["Seed"] = 6 });
            bag.Seed.Should().Be(6);
            bag.Sets.Should().Be(1);
        }
    }
}
