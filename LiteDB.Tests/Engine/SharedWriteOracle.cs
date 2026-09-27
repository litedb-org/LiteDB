using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Tests.Engine;

/// <summary>Independent expected rows for the shared-write workload, including secondary-index membership.</summary>
internal static class SharedWriteOracle
{
    private static readonly string[] Fields = { "_id", "task_id", "doc_number", "timestamp", "data" };

    internal static void Verify(ILiteCollection<BsonDocument> collection, int workers, int perWorker,
        DateTime startedUtc, DateTime finishedUtc)
    {
        var all = collection.FindAll().ToArray();
        VerifyResults(all, worker =>
        {
            var query = collection.Query().Where(Query.EQ("task_id", worker));
            var plan = query.GetPlan()["index"];
            // EnsureIndex("task_id") generates the persisted name "taskid".
            Require(plan["name"].AsString == "taskid" && plan["expr"].AsString == "$.task_id" &&
                plan["mode"].AsString.StartsWith("INDEX SEEK", StringComparison.Ordinal),
                "task_id queries must use the secondary index seek");
            return query.ToArray();
        }, workers, perWorker, startedUtc, finishedUtc);
    }

    internal static void VerifyResults(BsonDocument[] all, Func<int, BsonDocument[]> indexed,
        int workers, int perWorker, DateTime startedUtc, DateTime finishedUtc)
    {
        Require(all.Length == workers * perWorker, "total document count differs");
        foreach (var row in all) VerifyRow(row, workers, perWorker, startedUtc, finishedUtc);
        Require(all.Select(row => row["_id"].AsObjectId).Distinct().Count() == all.Length,
            "document IDs must be unique");

        for (var worker = 1; worker <= workers; worker++)
        {
            var expected = all.Where(row => row["task_id"].AsInt32 == worker)
                .OrderBy(row => row["doc_number"].AsInt32).ToArray();
            Require(expected.Select(row => row["doc_number"].AsInt32).SequenceEqual(Enumerable.Range(0, perWorker)),
                "each worker must contain every ordinal exactly once");
            var found = indexed(worker);
            Require(found.Length == perWorker, "secondary-index document count differs");
            foreach (var row in found) VerifyRow(row, workers, perWorker, startedUtc, finishedUtc);
            var ordered = found.OrderBy(row => row["doc_number"].AsInt32).ToArray();
            for (var ordinal = 0; ordinal < perWorker; ordinal++)
                Require(BsonSerializer.Serialize(ordered[ordinal]).SequenceEqual(BsonSerializer.Serialize(expected[ordinal])),
                    "secondary-index documents differ from the complete validated scan");
        }
    }

    private static void VerifyRow(BsonDocument row, int workers, int perWorker,
        DateTime startedUtc, DateTime finishedUtc)
    {
        Require(row.Keys.Count == Fields.Length && Fields.All(row.ContainsKey), "document field shape differs");
        Require(row["_id"].IsObjectId, "document ID must be an ObjectId");
        Require(row["task_id"].IsInt32 && row["doc_number"].IsInt32, "worker and ordinal must be Int32 values");
        var worker = row["task_id"].AsInt32;
        var ordinal = row["doc_number"].AsInt32;
        Require(worker >= 1 && worker <= workers && ordinal >= 0 && ordinal < perWorker,
            "worker or ordinal is outside the expected workload");
        Require(row["data"].IsString && row["data"].AsString == $"Data from task {worker}, document {ordinal}",
            "document payload differs from the independently expected string");
        Require(row["timestamp"].IsDateTime, "timestamp must retain its BSON DateTime type");
        var timestamp = row["timestamp"].AsDateTime.ToUniversalTime();
        // BSON dates round to milliseconds; allow that representation boundary,
        // not timestamps belonging to another run or a default/missing value.
        Require(timestamp >= startedUtc.AddMilliseconds(-1) && timestamp <= finishedUtc.AddMilliseconds(1),
            "timestamp is outside the worker run");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Shared-write oracle: " + message);
    }
}
