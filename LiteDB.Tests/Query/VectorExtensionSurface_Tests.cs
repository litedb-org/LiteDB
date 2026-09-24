using FluentAssertions;
using LiteDB;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class VectorExtensionSurface_Tests
    {
        private class VectorDocument
        {
            public int Id { get; set; }

            public float[] Embedding { get; set; }
        }

        [Fact]
        public void Collection_Extension_Produces_Vector_Index_Plan()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("vectors");

            collection.Insert(new VectorDocument { Id = 1, Embedding = new[] { 1f, 0f } });
            collection.Insert(new VectorDocument { Id = 2, Embedding = new[] { 0f, 1f } });

            collection.EnsureIndex(x => x.Embedding, new VectorIndexOptions(2));

            var plan = collection.Query()
                .WhereNear(x => x.Embedding, new[] { 1f, 0f }, maxDistance: 0.25)
                .GetPlan();

            plan["index"]["mode"].AsString.Should().Be("VECTOR INDEX SEARCH");
            plan["index"]["expr"].AsString.Should().Be("$.Embedding");
        }

        [Fact]
        public void Repository_Extension_Delegates_To_Vector_Index_Implementation()
        {
            using var db = new LiteDatabase(":memory:");
            ILiteRepository repository = new LiteRepository(db);

            repository.EnsureIndex<VectorDocument, float[]>(x => x.Embedding, new VectorIndexOptions(2));

            var plan = repository.Query<VectorDocument>()
                .WhereNear(x => x.Embedding, new[] { 1f, 0f }, maxDistance: 0.25)
                .GetPlan();

            plan["index"]["mode"].AsString.Should().Be("VECTOR INDEX SEARCH");
            plan["index"]["expr"].AsString.Should().Be("$.Embedding");
        }
    }
}
