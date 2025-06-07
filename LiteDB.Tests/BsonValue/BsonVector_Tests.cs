using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.BsonValue_Types;

public class BsonVector_Tests
{

    private static readonly Collation _collation = Collation.Binary;
    private static readonly BsonDocument _root = new BsonDocument();

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
            .WhereNear("Embedding", target, maxDistance: .28)
            .ToList();

        results.Should().NotBeEmpty();
        results.Select(x => x.Id).Should().Contain(1);
        results.Select(x => x.Id).Should().NotContain(2);
        results.Select(x => x.Id).Should().NotContain(3); // too far away
    }

    [Fact]
    public void VectorSim_ExpressionQuery_WorksViaSQL()
    {
        using var db = new LiteDatabase(":memory:");
        var col = db.GetCollection("vectors");

        col.Insert(new BsonDocument
        {
            ["_id"] = 1,
            ["Embedding"] = new BsonVector(new float[] { 1.0f, 0.0f })
        });
        col.Insert(new BsonDocument
        {
            ["_id"] = 2,
            ["Embedding"] = new BsonVector(new float[] { 0.0f, 1.0f })
        });
        col.Insert(new BsonDocument
        {
            ["_id"] = 3,
            ["Embedding"] = new BsonVector(new float[] { 1.0f, 1.0f })
        });

        //var query = "SELECT * FROM vectors WHERE vector_sim([1.0, 0.0], $.Embedding) < 0.3";
        //var results = db.Execute(query).ToList();
        var expr = BsonExpression.Create("VECTOR_SIM($.Embedding, [1.0, 0.0]) < 0.25");

        var results = db
            .GetCollection("vectors")
            .Find(expr)
            .ToList();

        results.Select(r => r["_id"].AsInt32).Should().Contain(1);
        results.Select(r => r["_id"].AsInt32).Should().NotContain(2);
        results.Select(r => r["_id"].AsInt32).Should().NotContain(3); // cosine ~ 0.293
    }

    [Fact]
    public void VectorSim_ReturnsZero_ForIdenticalVectors()
    {
        var left = new BsonArray { 1.0, 0.0 };
        var right = new BsonVector(new float[] { 1.0f, 0.0f });

        var result = BsonExpressionMethods.VECTOR_SIM(left, right);

        Assert.NotNull(result);
        Assert.True(result.IsDouble);
        Assert.Equal(0.0, result.AsDouble, 6); // Cosine distance = 0.0
    }

    [Fact]
    public void VectorSim_ReturnsOne_ForOrthogonalVectors()
    {
        var left = new BsonArray { 1.0, 0.0 };
        var right = new BsonVector(new float[] { 0.0f, 1.0f });

        var result = BsonExpressionMethods.VECTOR_SIM(left, right);

        Assert.NotNull(result);
        Assert.True(result.IsDouble);
        Assert.Equal(1.0, result.AsDouble, 6); // Cosine distance = 1.0
    }

    [Fact]
    public void VectorSim_ReturnsNull_ForInvalidInput()
    {
        var left = new BsonArray { "a", "b" };
        var right = new BsonVector(new float[] { 1.0f, 0.0f });

        var result = BsonExpressionMethods.VECTOR_SIM(left, right);

        Assert.True(result.IsNull);
    }

    [Fact]
    public void VectorSim_ReturnsNull_ForMismatchedLengths()
    {
        var left = new BsonArray { 1.0, 2.0, 3.0 };
        var right = new BsonVector(new float[] { 1.0f, 2.0f });

        var result = BsonExpressionMethods.VECTOR_SIM(left, right);

        Assert.True(result.IsNull);
    }




}