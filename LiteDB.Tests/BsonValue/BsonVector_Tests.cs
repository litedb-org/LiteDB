using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.BsonValue_Types;

public class BsonVector_Tests
{
    [Fact]
    public void BsonVector_RoundTrip_Success()
    {
        var original = new BsonDocument
        {
            ["vec"] = new BsonVector(new float[] { 1.0f, 2.5f, -3.75f })
        };

        var bytes = BsonSerializer.Serialize(original);
        var deserialized = BsonSerializer.Deserialize(bytes);

        var vec = deserialized["vec"].AsVector;
        Assert.Equal(3, vec.Length);
        Assert.Equal(1.0f, vec[0]);
        Assert.Equal(2.5f, vec[1]);
        Assert.Equal(-3.75f, vec[2]);
    }

    private class VectorDoc
    {
        public int Id { get; set; }
        public float[] Embedding { get; set; }
    }

    [Fact]
    public void VectorSim_Query_ReturnsExpectedNearest()
    {
        using var db = new LiteDatabase(":memory:");
        var col = db.GetCollection<VectorDoc>("vectors");

        // Insert vectorized documents
        col.Insert(new VectorDoc { Id = 1, Embedding = new float[] { 1.0f, 0.0f } });
        col.Insert(new VectorDoc { Id = 2, Embedding = new float[] { 0.0f, 1.0f } });
        col.Insert(new VectorDoc { Id = 3, Embedding = new float[] { 1.0f, 1.0f } });

        // Create index on the embedding field (if applicable to your implementation)
        col.EnsureIndex("Embedding", "Embedding");

        // Query: Find vectors nearest to [1, 0]
        var target = new float[] { 1.0f, 0.0f };
        var results = col.Query()
            .WhereNear("Embedding", target, maxDistance: 1.5)
            .ToList();

        results.Should().NotBeEmpty();
        results.Select(x => x.Id).Should().Contain(1);
        results.Select(x => x.Id).Should().Contain(3);
        results.Select(x => x.Id).Should().NotContain(2); // too far away
    }

}