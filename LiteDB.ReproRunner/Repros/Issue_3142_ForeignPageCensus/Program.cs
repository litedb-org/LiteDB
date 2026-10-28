using LiteDB;
using LiteDB.Vector;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3142_ForeignPageCensus;

internal static class Program
{
    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")
            ?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
        var directory = Path.Combine(Path.GetTempPath(), "hnswe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var filename = Path.Combine(directory, "vectors.db");
            var payload = new string('x', 40000);
            var reproduced = false;
            using (var db = new LiteDatabase(filename))
            {
                db.CheckpointSize = 0;
                var docs = db.GetCollection("docs");
                for (var id = 1; id <= 65; id++) docs.Insert(Document(id));
                docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2));
                db.Checkpoint();
                if (!db.BeginTrans() || !docs.Update(Document(1)))
                    throw new InvalidOperationException("Writer pin control failed.");
                Task.Run(() => db.GetCollection("foreign").Insert(new BsonDocument
                    { ["_id"] = 1, ["payload"] = payload })).GetAwaiter().GetResult();
                try
                {
                    if (!docs.Delete(2)) throw new InvalidOperationException("Delete returned false.");
                }
                catch (EndOfStreamException) { reproduced = true; }
                if (!reproduced)
                {
                    if (!db.Commit() || docs.Count() != 64 || docs.FindById(2) != null)
                        throw new InvalidOperationException("Delete commit control failed.");
                    if (db.GetCollection("foreign").FindById(1)["payload"].AsString != payload)
                        throw new InvalidOperationException("Foreign commit lost while engine live.");
                }
            }
            using (var db = new LiteDatabase(filename))
            {
                var expected = Enumerable.Range(1, 65).Where(id => reproduced || id != 2);
                if (!db.GetCollection("docs").FindAll().Select(doc => doc["_id"].AsInt32).OrderBy(id => id).SequenceEqual(expected) ||
                    db.GetCollection("foreign").FindById(1)["payload"].AsString != payload)
                    throw new InvalidOperationException("Cold recovery lost committed data or published partial deletion.");
            }
            var message = reproduced ? "BUG_VERIFIED" : "FIXED_VERIFIED";
            Console.WriteLine(message);
            host.SendResult(reproduced, message);
            return reproduced ? 0 : 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            host.SendResult(false, "Unexpected harness or database failure", new { Exception = error.ToString() });
            return 1;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static BsonDocument Document(int id) => new BsonDocument
    {
        ["_id"] = id, ["Embedding"] = new BsonArray(new BsonValue[] { 1f, id })
    };
}
