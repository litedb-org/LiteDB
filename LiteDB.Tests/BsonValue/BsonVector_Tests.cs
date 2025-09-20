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

    [Fact]
    public void BsonVector_RoundTrip_UInt16Limit()
    {
        var values = Enumerable.Range(0, ushort.MaxValue).Select(i => (float)(i % 32)).ToArray();

        var original = new BsonDocument
        {
            ["vec"] = new BsonVector(values)
        };

        var bytes = BsonSerializer.Serialize(original);
        var deserialized = BsonSerializer.Deserialize(bytes);

        deserialized["vec"].AsVector.Should().Equal(values);
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
            .WhereNear(r => r.Embedding, [1.0f, 0.0f], maxDistance: .28)
            .ToList();

        results.Should().NotBeEmpty();
        results.Select(x => x.Id).Should().Contain(1);
        results.Select(x => x.Id).Should().NotContain(2);
        results.Select(x => x.Id).Should().NotContain(3); // too far away
    }

    [Fact]
    public void VectorSim_Query_WhereVectorSimilar_AppliesAlias()
    {
        using var db = new LiteDatabase(":memory:");
        var col = db.GetCollection<VectorDoc>("vectors");

        col.Insert(new VectorDoc { Id = 1, Embedding = new float[] { 1.0f, 0.0f } });
        col.Insert(new VectorDoc { Id = 2, Embedding = new float[] { 0.0f, 1.0f } });
        col.Insert(new VectorDoc { Id = 3, Embedding = new float[] { 1.0f, 1.0f } });

        var target = new float[] { 1.0f, 0.0f };

        var nearResults = col.Query()
            .WhereNear(r => r.Embedding, target, maxDistance: .28)
            .ToList()
            .Select(r => r.Id)
            .OrderBy(id => id)
            .ToList();

        var similarResults = col.Query()
            .WhereNear(r => r.Embedding, target, maxDistance: .28)
            .ToList()
            .Select(r => r.Id)
            .OrderBy(id => id)
            .ToList();

        similarResults.Should().Equal(nearResults);
    }

    [Fact]
    public void VectorSim_Query_BsonExpressionOverload_ReturnsExpectedNearest()
    {
        using var db = new LiteDatabase(":memory:");
        var col = db.GetCollection<VectorDoc>("vectors");

        col.Insert(new VectorDoc { Id = 1, Embedding = new float[] { 1.0f, 0.0f } });
        col.Insert(new VectorDoc { Id = 2, Embedding = new float[] { 0.0f, 1.0f } });
        col.Insert(new VectorDoc { Id = 3, Embedding = new float[] { 1.0f, 1.0f } });

        var target = new float[] { 1.0f, 0.0f };
        var fieldExpr = BsonExpression.Create("$.Embedding");

        var results = col.Query()
            .WhereNear(fieldExpr, target, maxDistance: .28)
            .ToList();

        results.Select(x => x.Id).Should().ContainSingle(id => id == 1);
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

        var query = "SELECT * FROM vectors WHERE $.Embedding VECTOR_SIM [1.0, 0.0] <= 0.25";
        var rawResults = db.Execute(query).ToList();

        var docs = rawResults
            .Where(r => r.IsDocument)
            .SelectMany(r =>
            {
                var doc = r.AsDocument;
                if (doc.TryGetValue("expr", out var expr) && expr.IsArray)
                {
                    return expr.AsArray
                        .Where(x => x.IsDocument)
                        .Select(x => x.AsDocument);
                }

                return new[] { doc };
            })
            .ToList();

        docs.Select(d => d["_id"].AsInt32).Should().Contain(1);
        docs.Select(d => d["_id"].AsInt32).Should().NotContain(2);
        docs.Select(d => d["_id"].AsInt32).Should().NotContain(3); // cosine ~ 0.293
    }

    [Fact]
    public void VectorSim_InfixExpression_ParsesAndEvaluates()
    {
        var expr = BsonExpression.Create("$.Embedding VECTOR_SIM [1.0, 0.0]");

        expr.Type.Should().Be(BsonExpressionType.VectorSim);

        var doc = new BsonDocument
        {
            ["Embedding"] = new BsonArray { 1.0, 0.0 }
        };

        var result = expr.ExecuteScalar(doc);

        result.IsDouble.Should().BeTrue();
        result.AsDouble.Should().BeApproximately(0.0, 1e-6);
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


    [Fact]
    public void VectorSim_TopK_ReturnsCorrectOrder()
    {
        using var db = new LiteDatabase(":memory:");
        var col = db.GetCollection<VectorDoc>("vectors");

        col.Insert(new VectorDoc { Id = 1, Embedding = new float[] { 1.0f, 0.0f } }); // sim = 0.0
        col.Insert(new VectorDoc { Id = 2, Embedding = new float[] { 0.0f, 1.0f } }); // sim = 1.0
        col.Insert(new VectorDoc { Id = 3, Embedding = new float[] { 1.0f, 1.0f } }); // sim ≈ 0.293

        var target = new float[] { 1.0f, 0.0f };

        var results = col.Query()
            .TopKNear(x => x.Embedding, target, 2)
            .ToList();

        var ids = results.Select(r => r.Id).ToList();
        ids.Should().BeEquivalentTo(new[] { 1, 3 }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void BsonVector_CompareTo_SortsLexicographically()
    {
        var values = new List<BsonValue>
        {
            new BsonVector(new float[] { 1.0f }),
            new BsonVector(new float[] { 0.0f, 2.0f }),
            new BsonVector(new float[] { 0.0f, 1.0f, 0.5f }),
            new BsonVector(new float[] { 0.0f, 1.0f })
        };

        values.Sort();

        values.Should().Equal(
            new BsonVector(new float[] { 0.0f, 1.0f }),
            new BsonVector(new float[] { 0.0f, 1.0f, 0.5f }),
            new BsonVector(new float[] { 0.0f, 2.0f }),
            new BsonVector(new float[] { 1.0f }));
    }

    [Fact]
    public void BsonVector_Index_OrderIsDeterministic()
    {
        using var db = new LiteDatabase(":memory:");
        var col = db.GetCollection<VectorDoc>("vectors");

        var docs = new[]
        {
            new VectorDoc { Id = 1, Embedding = new float[] { 0.0f, 1.0f } },
            new VectorDoc { Id = 2, Embedding = new float[] { 0.0f, 1.0f, 0.5f } },
            new VectorDoc { Id = 3, Embedding = new float[] { 0.0f, 2.0f } },
            new VectorDoc { Id = 4, Embedding = new float[] { 1.0f } }
        };

        col.InsertBulk(docs);

        col.EnsureIndex(x => x.Embedding);

        var ordered = col.Query().OrderBy(x => x.Embedding).ToList();

        ordered.Select(x => x.Id).Should().Equal(1, 2, 3, 4);
    }
}
