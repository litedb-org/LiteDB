using System.Reflection;
using LiteDB;
using LiteDB.Engine;

internal static class SharedFollowupHarness
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static bool TryRun(string mode, string filename, string? password)
    {
        if (!mode.StartsWith("followup-", StringComparison.Ordinal)) return false;
        using var engine = new SharedEngine(new EngineSettings { Filename = filename, Password = password });
        if (mode == "followup-generations")
        {
            var readers = new Dictionary<int, IBsonDataReader>();
            try
            {
                while (Console.ReadLine() is string command && command != "done")
                {
                    var parts = command.Split(':');
                    var revision = int.Parse(parts[1]);
                    if (parts[0] == "open") readers.Add(revision, engine.Query("docs", new Query()));
                    else
                    {
                        using var reader = readers[revision];
                        var id = 0;
                        while (reader.Read())
                        {
                            var doc = reader.Current;
                            var payload = new string((char)('a' + id % 26), 3000) + ":" + revision;
                            if (doc["_id"].AsInt32 != id || doc["value"].AsInt32 != revision || doc["payload"].AsString != payload)
                                throw new Exception("Changed snapshot " + revision + " at " + id);
                            id++;
                        }
                        if (id != 32) throw new Exception("Missing snapshot records");
                        readers.Remove(revision);
                    }
                    Console.WriteLine(command);
                }
            }
            finally { foreach (var reader in readers.Values) reader.Dispose(); }
        }
        else if (mode == "followup-wait")
        {
            var turn = typeof(SharedEngine).GetField("_turnstile", Private)!.GetValue(engine)!;
            var mutex = (Mutex)typeof(SharedEngine).GetField("_mutex", Private)!.GetValue(engine)!;
            turn.GetType().GetProperty("BeforeMainWait", Private)!.SetValue(turn, (Action)(() => Console.WriteLine("ready")));
            try { turn.GetType().GetMethod("Wait")!.Invoke(turn, new object[] { mutex }); }
            catch (TargetInvocationException ex) when (ex.InnerException is AbandonedMutexException) { }
            mutex.ReleaseMutex();
        }
        else if (mode == "followup-owner")
        {
            engine.Insert("ack", new[] { new BsonDocument { ["_id"] = 1, ["value"] = 42 } }, BsonAutoId.Int32);
            var owner = typeof(SharedEngine).GetField("_owner", Private)!.GetValue(engine)!;
            owner.GetType().GetMethod("Enter")!.Invoke(owner, new object[] { true });
            Console.WriteLine("ready");
            Console.ReadLine();
            owner.GetType().GetMethod("Exit")!.Invoke(owner, new object[] { -1 });
        }
        else if (mode == "followup-pin")
        {
            typeof(SharedEngine).GetProperty("PinHoldLimit", Private)!.SetValue(engine, TimeSpan.FromMinutes(1));
            typeof(SharedEngine).GetProperty("PinIdleLimit", Private)!.SetValue(engine, TimeSpan.FromMinutes(1));
            using var reader = engine.Query("docs", new Query());
            engine.Insert("pin", new[] { new BsonDocument { ["_id"] = 1, ["value"] = 0 } }, BsonAutoId.Int32);
            Console.WriteLine("ready");
            for (var i = 1; ; i++)
                engine.Update("pin", new[] { new BsonDocument { ["_id"] = 1, ["value"] = i } });
        }
        else throw new ArgumentException(mode);
        Console.WriteLine("done");
        return true;
    }
}
