using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace LiteDB.Tests.Issues
{
    // Keep this workload compatible with C# 7.2 so the historical runner executes
    // the same public-API/data checks under Mono's original Dictionary semantics.
    internal static class Issue1472_CompoundQueryScenario
    {
        public static void Run(string filename, Action<string> report = null)
        {
            Exception queryFailure = null;
            using (var db = new LiteDatabase(filename))
            using (var start = new ManualResetEventSlim())
            {
                var rows = db.GetCollection("rows");
                Check(rows.Insert(Enumerable.Range(1, 100).Select(Document)) == 100, "seed insert count");
                rows.EnsureIndex("value");
                VerifyQuery(rows);

                var writer = Task.Factory.StartNew(() =>
                {
                    start.Wait();
                    for (var batch = 0; batch < 100; batch++)
                    {
                        Check(rows.Insert(Enumerable.Range(101 + batch * 10, 10).Select(Document)) == 10,
                            "bulk insert count");
                        Thread.Yield();
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                var readers = Enumerable.Range(0, 8).Select(worker => Task.Factory.StartNew(() =>
                {
                    start.Wait();
                    for (var round = 0; round < 200; round++)
                    {
                        VerifyQuery(rows);
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                start.Set();
                try
                {
                    Task.WaitAll(readers.Concat(new[] { writer }).ToArray());
                }
                catch (AggregateException error)
                {
                    queryFailure = error;
                }
                // WaitAll joins every reader and the writer even when queries fail.
                // Always check committed data before surfacing the historical error.
                VerifyLedger(rows, 1100);
            }
            using (var reopened = new LiteDatabase(filename))
            {
                var rows = reopened.GetCollection("rows");
                VerifyLedger(rows, 1100);
                rows.Insert(Document(1101));
            }
            using (var reopened = new LiteDatabase(filename))
            {
                VerifyLedger(reopened.GetCollection("rows"), 1101);
            }
            report?.Invoke("PERSISTENCE_1472: seed, 1000 bulk-inserted rows, recovery write, and two reopen ledgers passed");
            if (queryFailure != null)
            {
                ExceptionDispatchInfo.Capture(queryFailure).Throw();
            }
        }

        private static void VerifyQuery(ILiteCollection<BsonDocument> collection)
        {
            // Each call owns its parameter document. Identical values isolate the
            // reported enumeration error from a different cross-query value race.
            var parameters = new BsonDocument { ["value"] = 1, ["minimum"] = 1 };
            var query = BsonExpression.Create("value=@value AND _id>=@minimum", parameters);
            var rows = collection.Find(query).OrderBy(row => row["_id"].AsInt32).ToArray();
            Check(rows.Select(row => row["_id"].AsInt32).SequenceEqual(
                Enumerable.Range(1, 100).Where(id => id % 4 == 1)), "compound-query IDs");
            foreach (var row in rows)
            {
                Check(row["payload"].AsString == "row" + row["_id"].AsInt32, "query payload");
            }
            Check(parameters.Count == 2 && parameters["value"].AsInt32 == 1 &&
                parameters["minimum"].AsInt32 == 1, "caller parameter isolation");
        }

        private static BsonDocument Document(int id)
        {
            return new BsonDocument
            {
                ["_id"] = id,
                ["value"] = id <= 100 ? id % 4 : -1,
                ["payload"] = "row" + id
            };
        }

        private static void VerifyLedger(ILiteCollection<BsonDocument> collection, int count)
        {
            var rows = collection.FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
            Check(rows.Select(row => row["_id"].AsInt32).SequenceEqual(Enumerable.Range(1, count)),
                "persisted row IDs");
            foreach (var row in rows)
            {
                var id = row["_id"].AsInt32;
                Check(row["payload"].AsString == "row" + id &&
                    row["value"].AsInt32 == (id <= 100 ? id % 4 : -1), "persisted row contents");
            }
        }

        private static void Check(bool condition, string description)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Issue1472 integrity check failed: " + description);
            }
        }
    }
}
