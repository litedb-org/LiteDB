using System.Linq;
using FluentAssertions;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class Issue2881_VectorReadBounds_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Bounded_vector_reads_preserve_multi_page_documents_and_external_vectors(bool checkpoint)
        {
            const int dimensions = 3000;
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection("docs");
            var target = new float[dimensions];
            target[0] = 1;
            var opposite = new float[dimensions];
            opposite[0] = -1;
            var payload = new string('x', 25000);
            collection.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonVector(target), ["Payload"] = payload },
                new BsonDocument { ["_id"] = 2, ["Embedding"] = new BsonVector(opposite), ["Payload"] = payload }
            });
            collection.EnsureIndex("vectors", "$.Embedding", new VectorIndexOptions(dimensions));
            if (checkpoint) db.Checkpoint();

            var nearest = collection.Query().TopKNearWithScore("Embedding", target, 2).ToArray();
            nearest.Select(x => x.Document["_id"].AsInt32).Should().Equal(1, 2);
            nearest.Select(x => x.Score).Should().Equal(0d, 2d);
            nearest.Should().OnlyContain(x => x.Document["Payload"].AsString == payload);

            var filtered = collection.Query().Where(x => x["_id"] == 2)
                .TopKNearWithScore("Embedding", target, 2).Single();
            filtered.Score.Should().Be(2);
            filtered.Document["Embedding"].AsVector.Should().Equal(opposite);
            filtered.Document["Payload"].AsString.Should().Be(payload);

            using var sorted = db.Execute("SELECT $ FROM docs ORDER BY VECTOR_SIM($.Embedding, @q)",
                new BsonDocument { ["q"] = new BsonVector(target) });
            var results = sorted.ToArray();
            results.Select(x => x["_id"].AsInt32).Should().Equal(1, 2);
            results.Should().OnlyContain(x => x["Payload"].AsString == payload);
        }
    }
}
