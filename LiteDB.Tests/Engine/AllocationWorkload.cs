using System;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Tests.Engine
{
    // Public-API workload shared with the historical regression proof and child process.
    internal static class AllocationWorkload
    {
        internal static LiteDatabase Open(string filename, string password, int pageLimit = 1000) =>
            new LiteDatabase(new LiteEngine(new EngineSettings
            {
                Filename = filename, Password = password, TransactionPageLimit = pageLimit,
                CompactStorage = CompactStorageMode.Legacy
            }));

        internal static BsonDocument Document(int id) => new BsonDocument
        {
            ["_id"] = id, ["key"] = "key-" + id,
            ["payload"] = new string((char)('a' + id % 20), 2500),
            ["nested"] = new BsonDocument { ["id"] = id, ["values"] = new BsonArray(id, "value") }
        };

        internal static void Seed(LiteDatabase db, string mode)
        {
            db.CheckpointSize = 0;
            foreach (var name in new[] { "a", "b", "cold" })
            {
                var col = db.GetCollection(name);
                col.Insert(Document(1));
                col.EnsureIndex("key");
            }
            if (mode == "free")
            {
                db.GetCollection("spare").Insert(Enumerable.Range(0, 80).Select(Document));
                db.DropCollection("spare");
            }
            db.Checkpoint();
        }

        internal static void Publish(LiteDatabase db, string mode, string completion = "abandon", Action<string> stage = null)
        {
            if (mode == "tail")
            {
                db.BeginTrans();
                db.GetCollection("b").Insert(Enumerable.Range(200, 8).Select(Document));
            }
            using var allocated = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Exception failure = null;
            var owner = new Thread(() =>
            {
                try
                {
                    db.BeginTrans();
                    db.GetCollection("a").Insert(Enumerable.Range(100, 12).Select(Document));
                    allocated.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("allocation owner");
                    if (completion == "commit") db.Commit();
                    if (completion == "rollback") db.Rollback();
                }
                catch (Exception ex) { failure = ex; allocated.Set(); }
            }) { IsBackground = true };
            owner.Start();
            try
            {
                if (!allocated.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("allocation barrier");
                if (failure != null) throw new InvalidOperationException("allocation failed", failure);
                stage?.Invoke("allocated");
                if (mode == "tail") db.Commit();
                else db.GetCollection("b").Insert(Enumerable.Range(200, 8).Select(Document));
                stage?.Invoke("committed");
                // Also exercise a callback-only header publication while A remains pending.
                db.UserVersion = 3082;
                stage?.Invoke("published");
            }
            finally
            {
                release.Set();
                if (!owner.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("allocation owner exit");
            }
            if (failure != null) throw new InvalidOperationException("allocation completion failed", failure);
        }

        internal static void Verify(LiteDatabase db, bool ownerCommitted = false, bool committed = true, bool headerPublished = true)
        {
            foreach (var name in new[] { "a", "b", "cold" })
            {
                var ids = new[] { 1 }.Concat(name == "b" && committed ? Enumerable.Range(200, 8) :
                    name == "a" && ownerCommitted ? Enumerable.Range(100, 12) : Enumerable.Empty<int>()).ToArray();
                var col = db.GetCollection(name);
                if (col.Query().Where(Query.EQ("key", "key-1")).GetPlan()["index"]["name"].AsString != "key")
                    throw new InvalidDataException("secondary index was not selected");
                var actual = col.FindAll().OrderBy(doc => doc["_id"].AsInt32).ToArray();
                if (actual.Length != ids.Length) throw new InvalidDataException("wrong rows: " + name);
                for (var i = 0; i < ids.Length; i++)
                {
                    var expected = Document(ids[i]);
                    if (!BsonSerializer.Serialize(actual[i]).SequenceEqual(BsonSerializer.Serialize(expected)))
                        throw new InvalidDataException("wrong complete document: " + name);
                    var indexed = col.Find(Query.EQ("key", "key-" + ids[i])).ToArray();
                    if (indexed.Length != 1 || indexed[0]["_id"] != ids[i])
                        throw new InvalidDataException("wrong index: " + name);
                }
                foreach (var absent in new[] { 100, 200, 999 }.Except(ids))
                    if (col.Find(Query.EQ("key", "key-" + absent)).Any()) throw new InvalidDataException("unexpected index key");
            }
            if (db.UserVersion != (headerPublished ? 3082 : 0)) throw new InvalidDataException("lost header callback");
        }
    }
}
