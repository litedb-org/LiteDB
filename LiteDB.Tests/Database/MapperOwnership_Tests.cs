using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    [CollectionDefinition(GlobalMapperCollection.Name, DisableParallelization = true)]
    public class GlobalMapperCollection
    {
        public const string Name = "BsonMapper.Global";
    }

    [Collection(GlobalMapperCollection.Name)]
    public class MapperOwnership_Tests
    {
        private sealed class Row
        {
            public int Id { get; set; }

            public string Name { get; set; }
        }

        private sealed class CustomValue
        {
            public string Value { get; set; }
        }

        private sealed class CustomMapper : BsonMapper
        {
            public string Marker { get; set; }

            protected override BsonMapper CreateCloneInstance()
            {
                return new CustomMapper { Marker = this.Marker };
            }
        }

        private sealed class Referenced
        {
            public int Id { get; set; }

            public int AlternateId { get; set; }
        }

        private sealed class Owner
        {
            public int Id { get; set; }

            public Referenced Reference { get; set; }
        }

        [Fact]
        public void Mapperless_databases_clone_global_configuration_independently()
        {
            var original = BsonMapper.Global;
            var global = new CustomMapper
            {
                EnumAsInteger = true,
                Marker = "cloned"
            };
            global.RegisterType<CustomValue>(x => x.Value, x => new CustomValue { Value = x.AsString });
            global.Entity<Row>().Field(x => x.Name, "global_name");
            global.Entity<Owner>().DbRef(x => x.Reference, "references");
            BsonMapper.Global = global;

            try
            {
                using var first = new LiteDatabase(":memory:");

                first.Mapper.Should().NotBeSameAs(global);
                first.Mapper.Should().BeOfType<CustomMapper>()
                    .Which.Marker.Should().Be("cloned");
                first.Mapper.EnumAsInteger.Should().BeTrue();
                first.Mapper.Serialize(new CustomValue { Value = "registered" }).AsString.Should().Be("registered");
                first.Mapper.ToDocument(new Row { Id = 1, Name = "mapped" })
                    .ContainsKey("global_name").Should().BeTrue();

                global.EnumAsInteger = false;
                global.Entity<Row>().Field(x => x.Name, "changed_name");
                global.Entity<Referenced>().Id(x => x.AlternateId);

                var reference = new Referenced { Id = 10, AlternateId = 20 };
                var clonedReference = first.Mapper.ToDocument(new Owner { Id = 1, Reference = reference });
                clonedReference["Reference"]["$id"].AsInt32.Should().Be(10);
                first.Mapper.EnumAsInteger.Should().BeTrue();
                first.Mapper.ToDocument(new Row { Id = 1, Name = "mapped" })
                    .ContainsKey("global_name").Should().BeTrue();

                using var second = new LiteDatabase(":memory:");

                second.Mapper.Should().NotBeSameAs(global);
                second.Mapper.Should().NotBeSameAs(first.Mapper);
                second.Mapper.EnumAsInteger.Should().BeFalse();
                second.Mapper.ToDocument(new Row { Id = 1, Name = "mapped" })
                    .ContainsKey("changed_name").Should().BeTrue();
                var secondReference = second.Mapper.ToDocument(new Owner { Id = 2, Reference = reference });
                secondReference["Reference"]["$id"].AsInt32.Should().Be(20);
            }
            finally
            {
                BsonMapper.Global = original;
            }
        }

        [Fact]
        public void Explicit_mapper_is_the_database_mapper()
        {
            var mapper = new BsonMapper();

            using var database = new LiteDatabase(":memory:", mapper);

            database.Mapper.Should().BeSameAs(mapper);
            database.Context.Mapper.Should().BeSameAs(mapper);
        }

        [Fact]
        public void Owned_collections_and_queries_share_the_database_context()
        {
            var mapper = new BsonMapper();
            using var database = new LiteDatabase(":memory:", mapper);

            var collection = (LiteCollection<Row>)database.GetCollection<Row>("rows");
            var included = (LiteCollection<Row>)collection.Include(x => x.Name);
            var query = (LiteQueryable<Row>)collection.Query();
            var projection = (LiteQueryable<string>)query.Select(x => x.Name);

            collection.Context.Should().BeSameAs(database.Context);
            included.Context.Should().BeSameAs(database.Context);
            query.Context.Should().BeSameAs(database.Context);
            projection.Context.Should().BeSameAs(database.Context);
            collection.Context.Mapper.Should().BeSameAs(mapper);
        }
    }
}
