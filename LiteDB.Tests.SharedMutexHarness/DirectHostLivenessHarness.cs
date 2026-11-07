using System.Collections.Concurrent;
using LiteDB;

internal static class DirectHostLivenessHarness
{
    internal static void Run(string filename, string password)
    {
        foreach (var writers in new[] { 1, 4, 8 })
        {
            var path = filename + "." + writers;
            using (var database = Open(path, password))
            {
                Console.WriteLine("opened-" + writers);
                var rows = database.GetCollection("rows");
                rows.EnsureIndex("value", true);
                Console.WriteLine("indexed-" + writers);
                using var start = new ManualResetEventSlim();
                var errors = new ConcurrentQueue<Exception>();
                var threads = Enumerable.Range(0, writers).Select(worker => new Thread(() =>
                {
                    start.Wait();
                    try
                    {
                        for (var id = worker; id < 64; id += writers)
                            rows.Insert(Row(id));
                    }
                    catch (Exception error) { errors.Enqueue(error); }
                })).ToArray();
                foreach (var thread in threads) thread.Start();
                start.Set();
                foreach (var thread in threads)
                    if (!thread.Join(TimeSpan.FromSeconds(20)))
                        throw new TimeoutException("Direct durable writer did not finish: " + writers);
                if (!errors.IsEmpty) throw new AggregateException(errors);
                Console.WriteLine("committed-" + writers);
                Verify(rows);
                database.BeginTrans();
                rows.Insert(Row(100));
                database.Rollback();
                Verify(rows);
                database.Checkpoint();
                Console.WriteLine("checkpointed-" + writers);
            }
            Console.WriteLine("closed-" + writers);
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var database = Open(path, password);
                Verify(database.GetCollection("rows"));
            }
            Console.WriteLine("reopened-" + writers);
        }
        Console.WriteLine("done");
    }

    private static LiteDatabase Open(string path, string password) => new(new ConnectionString
    {
        Filename = path, Password = password, Connection = ConnectionType.Direct,
        DurableCommits = true, Collation = new Collation(127, System.Globalization.CompareOptions.Ordinal)
    });

    private static BsonDocument Row(int id) => new()
    {
        ["_id"] = id, ["value"] = id * 17, ["payload"] = new string((char)('a' + id % 26), 200)
    };

    private static void Verify(ILiteCollection<BsonDocument> rows)
    {
        if (rows.Count() != 64 || rows.FindById(100) != null)
            throw new InvalidOperationException("Direct durable rows or rollback changed");
        for (var id = 0; id < 64; id++)
        {
            var row = rows.FindOne(Query.EQ("value", id * 17));
            if (row == null || row["_id"].AsInt32 != id || row["payload"].AsString != Row(id)["payload"].AsString)
                throw new InvalidOperationException("Direct durable contents/index changed: " + id);
        }
    }
}
