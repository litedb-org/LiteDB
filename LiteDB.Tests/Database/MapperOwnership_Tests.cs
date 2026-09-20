using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class MapperOwnership_Tests
    {
        private sealed class Row
        {
            public int Id { get; set; }

            public string Name { get; set; }
        }

        [Fact]
        public void Mapperless_databases_use_independent_local_mappers()
        {
            using var first = new LiteDatabase(":memory:");
            using var second = new LiteDatabase(":memory:");

            first.Mapper.Should().NotBeSameAs(BsonMapper.Global);
            second.Mapper.Should().NotBeSameAs(BsonMapper.Global);
            first.Mapper.Should().NotBeSameAs(second.Mapper);
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
