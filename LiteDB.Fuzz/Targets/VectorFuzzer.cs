using LiteDB.Engine;
using LiteDB.Vector;
using LiteDB.Tests.Mapper;

namespace LiteDB.Fuzz.Targets;

internal sealed class VectorFuzzer : IFuzzTarget
{
    private static readonly object Gate = new();

    public string Name => "vector";
    public string Description => "Deterministic vector-index churn with live/unique/score/order invariants and reopen/rebuild.";

    public Task RunAsync(FuzzContext context)
    {
        lock (Gate) Run(context);
        return Task.CompletedTask;
    }

    private static void Run(FuzzContext context)
    {
        var previous = VectorIndexService.LevelRandomFactory;
        VectorIndexService.LevelRandomFactory = () => new StableRandom(context.Seed);
        VerifyAllMetricDimensionPairs(context);
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "vector.db"));
        var dimensions = new[] { 2, 3, 8, 32, 256, 2048 }[Math.Abs(context.Seed % 6)];
        var metric = (VectorDistanceMetric)(Math.Abs(unchecked(context.Seed * 1103515245 + 12345)) % 3);
        var model = new Dictionary<int, VectorDocument>();
        var db = new LiteDatabase(file);
        var integrityChecks = 0;
        var searches = 0;
        try
        {
            var collection = db.GetCollection<VectorDocument>("vectors");
            collection.EnsureIndex("embedding_idx", BsonExpression.Create("$.Embedding"),
                new VectorIndexOptions((ushort)dimensions, metric));
            while (context.Next())
            {
                var id = context.Random.Next(1, 100);
                var operation = context.Random.Next(6);
                if (operation <= 2)
                {
                    var document = new VectorDocument { Id = id, Embedding = ValidVector(context.Random, dimensions) };
                    collection.Upsert(document);
                    model[id] = document;
                    context.Trace("vector-upsert", id);
                }
                else if (operation == 3)
                {
                    collection.Delete(id);
                    model.Remove(id);
                    context.Trace("vector-delete", id);
                }
                else if (operation == 4)
                {
                    InvalidVectorProbe(context, collection, dimensions);
                }
                else if (context.Steps % 13 == 0)
                {
                    db.Checkpoint();
                    db.Dispose();
                    db = new LiteDatabase(file);
                    collection = db.GetCollection<VectorDocument>("vectors");
                    context.Trace("vector-reopen");
                }
                Validate(context, collection, model, dimensions, metric);
                context.ObserveNovelty("vector-state", dimensions, metric, operation, model.Count / 8);
                searches++;
                if (context.Steps % 101 == 0)
                {
                    db.Checkpoint();
                    DatabaseIntegrityVerifier.Verify(context, file);
                    integrityChecks++;
                }
            }
            if (model.Count != 0)
            {
                db.Rebuild();
                collection = db.GetCollection<VectorDocument>("vectors");
                Validate(context, collection, model, dimensions, metric);
            }
            db.Checkpoint();
            DatabaseIntegrityVerifier.Verify(context, file);
            integrityChecks++;
            if (context.Steps >= 100)
                context.Check(searches >= context.Steps && integrityChecks > 0,
                    "Vector campaign missed ANN search or raw graph integrity paths.");
            context.Metrics["documents"] = model.Count;
            context.Metrics["dimensions"] = dimensions;
            context.Metrics["metric"] = metric;
            context.Metrics["searches"] = searches;
            context.Metrics["integrityChecks"] = integrityChecks;
        }
        finally
        {
            db.Dispose();
            VectorIndexService.LevelRandomFactory = previous;
        }
    }

    private static void Validate(FuzzContext context, ILiteCollection<VectorDocument> collection,
        Dictionary<int, VectorDocument> model, int dimensions, VectorDistanceMetric metric)
    {
        if (model.Count == 0) return;
        var target = ValidVector(context.Random, dimensions);
        var k = Math.Min(model.Count, context.Random.Next(1, Math.Min(model.Count, 12) + 1));
        var results = collection.Query().TopKNearWithScore("Embedding", target, k).ToArray();
        context.Check(results.Length == k, "Vector Top-K returned an unexpected result count.");
        FuzzOracle.VerifyDistinct(context, results.Select(result => result.Document.Id),
            EqualityComparer<int>.Default, "Vector search returned duplicate documents.");
        double? previousScore = null;
        foreach (var result in results)
        {
            context.Check(model.TryGetValue(result.Document.Id, out var live), "Vector search returned a deleted document.");
            context.Check(result.Score.HasValue, "A finite non-zero vector produced no score.");
            var expected = ReferenceScore(live.Embedding, target, metric);
            FuzzOracle.VerifyVectorScore(context, result.Score.Value, expected, 1e-5,
                $"Vector score mismatch for {result.Document.Id}: {result.Score} != {expected}.");
            if (previousScore.HasValue)
            {
                var ordered = metric == VectorDistanceMetric.DotProduct ? previousScore.Value >= result.Score.Value : previousScore.Value <= result.Score.Value;
                context.Check(ordered, "Vector results were not ordered by their metric.");
            }
            previousScore = result.Score;
        }
        var exact = model.Values.Select(document => (document.Id,
                Score: ReferenceScore(document.Embedding, target, metric)))
            .OrderBy(item => metric == VectorDistanceMetric.DotProduct ? -item.Score : item.Score)
            .ThenBy(item => item.Id).Take(k).Select(item => item.Id).ToHashSet();
        var recall = results.Count(result => exact.Contains(result.Document.Id)) / (double)k;
        context.Check(recall >= 0.5d, $"Vector ANN recall {recall:P0} was below the 50% quality floor.");
    }

    private static void InvalidVectorProbe(FuzzContext context, ILiteCollection<VectorDocument> collection, int dimensions)
    {
        var invalid = (context.Steps % 3) switch
        {
            0 => new float[Math.Max(1, dimensions - 1)],
            1 => Enumerable.Repeat(float.NaN, dimensions).ToArray(),
            _ => Enumerable.Repeat(float.PositiveInfinity, dimensions).ToArray()
        };
        var id = 1_000_000 + context.Steps;
        var count = collection.Count();
        Exception error = null;
        try { collection.Insert(new VectorDocument { Id = id, Embedding = invalid }); }
        catch (Exception caught) { error = caught; }
        context.Check(error == null || error is LiteException or ArgumentException,
            $"Invalid vector failed through {error?.GetType().Name}.");
        var results = collection.Query().TopKNearWithScore("Embedding", ValidVector(context.Random, dimensions),
            Math.Max(1, count + 1)).ToArray();
        if (error == null)
        {
            context.Check(collection.Count() == count + 1 && collection.FindById(id) != null,
                "Accepted invalid vector was not stored consistently.");
            context.Check(results.All(result => result.Document.Id != id),
                "An invalid vector entered ANN search results.");
            context.Check(collection.Delete(id), "Accepted invalid-vector probe could not be cleaned up.");
        }
        else
        {
            context.Check(collection.Count() == count && collection.FindById(id) == null,
                "Rejected vector changed collection state.");
        }
        context.Check(collection.Count() == count, "Invalid-vector probe leaked state.");
        context.Trace("invalid-vector", new { id, error = error?.GetType().Name ?? "accepted-unindexed" });
    }

    private static void VerifyAllMetricDimensionPairs(FuzzContext context)
    {
        var random = new StableRandom(context.Seed ^ 0x45d9f3b);
        var pairs = 0;
        foreach (var dimensions in new[] { 2, 3, 8, 32, 256, 2048 })
        foreach (var metric in Enum.GetValues<VectorDistanceMetric>())
        {
            using var db = new LiteDatabase(new MemoryStream());
            var rows = db.GetCollection<VectorDocument>("vectors");
            rows.EnsureIndex("embedding", "Embedding", new VectorIndexOptions((ushort)dimensions, metric));
            var documents = Enumerable.Range(1, 16).Select(id => new VectorDocument
            {
                Id = id, Embedding = ValidVector(random, dimensions)
            }).ToArray();
            rows.InsertBulk(documents);
            var target = ValidVector(random, dimensions);
            var actual = rows.Query().TopKNearWithScore("Embedding", target, 5).ToArray();
            var exact = documents.OrderBy(document => metric == VectorDistanceMetric.DotProduct
                    ? -ReferenceScore(document.Embedding, target, metric)
                    : ReferenceScore(document.Embedding, target, metric))
                .ThenBy(document => document.Id).Take(5).Select(document => document.Id).ToHashSet();
            var recall = actual.Count(result => exact.Contains(result.Document.Id)) / 5d;
            context.Check(recall >= 0.6d,
                $"Vector pair {dimensions}/{metric} recall {recall:P0} was below 60%.");
            foreach (var result in actual)
                FuzzOracle.VerifyVectorScore(context, result.Score.Value,
                    ReferenceScore(result.Document.Embedding, target, metric), 1e-5,
                    $"Independent vector score mismatch for {dimensions}/{metric}.");
            context.ObserveNovelty("vector-pair", dimensions, metric);
            pairs++;
        }
        context.Check(pairs == 18, "Vector campaign did not cover all dimension/metric pairs.");
        context.Metrics["explicitVectorPairs"] = pairs;
    }

    private static double ReferenceScore(float[] candidate, float[] target, VectorDistanceMetric metric)
    {
        double dot = 0, left = 0, right = 0, squared = 0;
        for (var i = 0; i < candidate.Length; i++)
        {
            var a = (double)candidate[i];
            var b = (double)target[i];
            dot += a * b;
            left += a * a;
            right += b * b;
            var difference = a - b;
            squared += difference * difference;
        }
        return metric switch
        {
            VectorDistanceMetric.DotProduct => dot,
            VectorDistanceMetric.Euclidean => Math.Sqrt(squared),
            VectorDistanceMetric.Cosine => 1d - dot / (Math.Sqrt(left) * Math.Sqrt(right)),
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
    }

    private static float[] ValidVector(Random random, int dimensions)
    {
        var vector = Enumerable.Range(0, dimensions).Select(_ => (float)(random.NextDouble() * 2d - 1d)).ToArray();
        if (vector.All(value => Math.Abs(value) < 1e-8f)) vector[0] = 1f;
        return vector;
    }

    private sealed class VectorDocument
    {
        public int Id { get; set; }
        public float[] Embedding { get; set; }
    }
}
