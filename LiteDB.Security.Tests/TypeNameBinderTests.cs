using System;
using Xunit;

namespace LiteDB.Security.Tests
{
    public class TypeNameBinderTests
    {
        public class BaseEntity { public string Name { get; set; } }
        public class DerivedEntity : BaseEntity { public int Number { get; set; } }
        public class Container { public BaseEntity Value { get; set; } }

        private sealed class AllowListBinder : ITypeNameBinder
        {
            public string GetName(Type type) => type == typeof(DerivedEntity) ? "approved" : throw new InvalidOperationException();
            public Type GetType(string name) => name == "approved" ? typeof(DerivedEntity) : null;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Safe_polymorphic_values_round_trip_with_default_or_allow_list_binder(bool custom)
        {
            var mapper = new BsonMapper();
            if (custom) mapper.TypeNameBinder = new AllowListBinder();
            var document = mapper.ToDocument(new Container { Value = new DerivedEntity { Name = "safe", Number = 42 } });
            var discriminator = document["Value"].AsDocument["_type"].AsString;
            if (custom) Assert.Equal("approved", discriminator);
            else Assert.Equal(typeof(DerivedEntity).FullName + ", " + typeof(DerivedEntity).Assembly.GetName().Name, discriminator);
            var result = mapper.ToObject<Container>(document);
            var value = Assert.IsType<DerivedEntity>(result.Value);
            Assert.Equal("safe", value.Name);
            Assert.Equal(42, value.Number);
        }

        [Fact]
        public void Allow_list_rejects_unregistered_assembly_qualified_names()
        {
            var mapper = new BsonMapper { TypeNameBinder = new AllowListBinder() };
            var failure = Assert.Throws<LiteException>(() => mapper.ToObject<object>(new BsonDocument
                { ["_type"] = typeof(DerivedEntity).AssemblyQualifiedName }));
            Assert.Equal(LiteException.INVALID_TYPED_NAME, failure.ErrorCode);
        }

        [Fact]
        public void Custom_binder_cannot_bypass_assignability()
        {
            var attempted = false;
            var mapper = new BsonMapper(type => { attempted = true; throw new InvalidOperationException(); })
            {
                TypeNameBinder = new AllowListBinder()
            };
            var failure = Assert.Throws<LiteException>(() => mapper.ToObject<Container>(
                new BsonDocument { ["_type"] = "approved" }));
            Assert.Equal(LiteException.DATA_TYPE_NOT_ASSIGNABLE, failure.ErrorCode);
            Assert.False(attempted);
        }

        [Fact]
        public void Allow_list_rejects_an_unregistered_generic_wrapper_before_instantiation()
        {
            var attempted = false;
            var mapper = new BsonMapper(type => { attempted = true; throw new InvalidOperationException(); })
            {
                TypeNameBinder = new AllowListBinder()
            };
            var failure = Assert.Throws<LiteException>(() => mapper.ToObject<object>(new BsonDocument
            {
                ["_type"] = typeof(System.Collections.Generic.Dictionary<string, System.Diagnostics.ProcessStartInfo>).AssemblyQualifiedName,
                ["entry"] = new BsonDocument()
            }));
            Assert.Equal(LiteException.INVALID_TYPED_NAME, failure.ErrorCode);
            Assert.False(attempted);
        }

        [Fact]
        public void Null_binder_is_rejected_without_replacing_the_default()
        {
            var mapper = new BsonMapper();
            Assert.Throws<ArgumentNullException>(() => mapper.TypeNameBinder = null);
            Assert.Same(DefaultTypeNameBinder.Instance, mapper.TypeNameBinder);
        }
    }
}
