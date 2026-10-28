using LiteDB;
using LiteDB.Vector;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3141_HnswConnectivity;

internal static class Program
{
    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var directory = Path.Combine(Path.GetTempPath(), "hnsw-3141-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var filename = Path.Combine(directory, "vectors.db");
            bool reproduced;
            using (var db = new LiteDatabase(filename))
            {
                var docs = db.GetCollection("docs");
                docs.Insert(Enumerable.Range(1, 40).Select(i => new BsonDocument
                {
                    ["_id"] = i, ["v"] = new BsonArray(1.0, 1.0)
                }));
                docs.EnsureIndex("vectors", "$.v", new VectorIndexOptions(2));
                var ann = docs.Query().TopKNear("v", new[] { 1f, 1f }, 40);
                if (ann.GetPlan()["index"]["name"].AsString != "vectors")
                    throw new InvalidOperationException("ANN did not use the vector index.");
                var exact = docs.Query().WhereNear("v", new[] { 1f, 1f }, double.MaxValue).ToArray();
                if (exact.Length != 40 || docs.Count() != 40)
                    throw new InvalidOperationException("Exact/document preservation control failed.");
                var hits = ann.ToArray();
                reproduced = hits.Length != 40;
                Console.WriteLine($"ANN={hits.Length}; exact={exact.Length}; ef=160>N=40");
                db.Checkpoint();
            }
            using (var reopened = new LiteDatabase(filename))
            {
                var hits = reopened.GetCollection("docs").Query().TopKNear("v", new[] { 1f, 1f }, 40).ToArray();
                if ((hits.Length != 40) != reproduced)
                    throw new InvalidOperationException("Reopen changed the observed graph outcome.");
                if (!reproduced && hits.Select(x => x["_id"].AsInt32).Distinct().Count() != 40)
                    throw new InvalidOperationException("Duplicate ANN results.");
            }
            var message = reproduced ? "DISCONNECTED_GRAPH_VERIFIED" : "FIXED_VERIFIED";
            Console.WriteLine(message);
            host.SendResult(reproduced, message);
            return reproduced ? 0 : 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            host.SendResult(false, "Harness or unexpected database failure", new { Exception = error.ToString() });
            return 1;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
