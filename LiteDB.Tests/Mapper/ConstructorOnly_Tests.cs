using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class ConstructorOnly_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        [Fact]
        public void Constructor_only_owns_creation_and_legacy_registration_restores_population()
        {
            var instantiatorCalls = 0;
            var factoryCalls = 0;
            var mapper = new BsonMapper(type =>
            {
                instantiatorCalls++;
                return null;
            });
            var raw = new BsonDocument { ["_id"] = 17, ["Name"] = "stored" };
            var owned = new Row { Id = 17, Name = "owned" };
            mapper.Entity<Row>().CtorOnly(doc => { factoryCalls++; return owned; });
            mapper.ToObject<Row>(raw).Should().BeSameAs(owned);
            owned.Name.Should().Be("owned");
            factoryCalls.Should().Be(1);
            instantiatorCalls.Should().Be(0);

            mapper.Entity<Row>().Ctor(doc => new Row { Name = "temporary" });
            mapper.ToObject<Row>(raw).Name.Should().Be("stored");
            instantiatorCalls.Should().Be(1);
        }

        [Fact]
        public void Null_factory_is_rejected_and_null_result_is_returned_without_retry()
        {
            var mapper = new BsonMapper();
            Action configure = () => mapper.Entity<Row>().CtorOnly(null);
            configure.Should().Throw<ArgumentNullException>();
            var calls = 0;
            mapper.Entity<Row>().CtorOnly(doc => { calls++; return null; });
            mapper.ToObject<Row>(new BsonDocument { ["_id"] = 17 }).Should().BeNull();
            calls.Should().Be(1);
        }
    }
}
