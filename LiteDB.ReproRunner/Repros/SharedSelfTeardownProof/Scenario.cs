using System.Diagnostics;
using System.Reflection;
using LiteDB;
using LiteDB.Engine;

namespace SharedSelfTeardownProof;

internal static class Scenario
{
    private const string Refusal = "Cannot reenter a shared connection from inside its executing core teardown.";
    internal static int Run(string mode, bool encrypted, string directory)
    {
        typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")
            ?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
        var password = encrypted ? "secret" : null;
        var file = Path.Combine(directory, "data.db");
        Fixture.Seed(file, password);
        if (mode == "control") OrdinaryRecursion(Path.Combine(directory, "ordinary.db"), password);
        var otherFile = Path.Combine(directory, "other.db");
        if (mode == "control") Fixture.Seed(otherFile, password);
        var data = Fixture.CallbackFile.Open(file);
        var log = Fixture.CallbackFile.Open(Path.Combine(directory, "data-log.db"));
        var outer = new SharedEngine(new EngineSettings
        { Filename = file, Password = password, DataStream = data, LogStream = log, ReadTransform = (_, value) => value });
        var mutex = (Mutex)Field(outer, "_mutex");
        var complete = new ManualResetEventSlim();
        Exception? failure = null, nested = null;
        var active = 0;
        var calls = 0;
        var exposed = false;
        var worker = new Thread(() =>
        {
            try
            {
                var reader = outer.Query("sentinel", new Query { ForUpdate = true });
                if (Probe(mutex)) throw new Exception("Fixture has no native ownership.");
                outer.Insert("rows", Enumerable.Range(100, 60).Select(Fixture.Row).ToArray(), BsonAutoId.Int32);
                data.Arm(() =>
                {
                    Interlocked.Increment(ref calls);
                    if (Probe(mutex)) throw new Exception("Callback entered without native ownership.");
                    Volatile.Write(ref active, 1);
                    try
                    {
                        if (mode == "getter") outer.Pragma("USER_VERSION");
                        else if (mode == "dispose") outer.Dispose();
                        else
                        {
                            using var other = new SharedEngine(new EngineSettings { Filename = otherFile, Password = password });
                            if (other.Pragma("USER_VERSION").AsInt32 != 0) throw new Exception("Other-file getter differs.");
                            other.Insert("rows", new[] { Fixture.Row(9) }, BsonAutoId.Int32);
                        }
                    }
                    catch (Exception error) { nested = error; }
                    Volatile.Write(ref active, 0);
                    // Never mistake the diagnostic probe's Join below for a getter
                    // wait when the fixed call has already returned/refused.
                    exposed = Probe(mutex);
                    if (mode == "dispose" && nested == null && exposed)
                    {
                        Console.WriteLine("EARLY_RELEASE_VERIFIED: foreign native acquisition succeeded inside the close callback before its return.");
                        Console.Out.Flush();
                        // Stop the isolated known-bad child at the witness: the original
                        // checkpoint must not continue through an unprotected write.
                        Environment.Exit(0);
                    }
                });
                reader.Dispose();
            }
            catch (Exception error) { failure = error; }
            finally { complete.Set(); }
        }) { IsBackground = true };
        worker.Start();
        var deadline = Stopwatch.StartNew();
        while (!complete.IsSet && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (mode == "getter" && Volatile.Read(ref active) == 1 && SelfWait(outer, worker, mutex))
            {
                Thread.Sleep(50);
                if (Volatile.Read(ref active) == 1 && SelfWait(outer, worker, mutex))
                {
                    Console.WriteLine("SELF_WAIT_VERIFIED: callback active; closing core exclusive owner is callback thread; " +
                        "callback thread blocked; foreign native acquisition excluded; both observations agree.");
                    // The enclosing process exits with this background worker still alive.
                    // Do not dispose its graph, release its mutex or inspect its live files.
                    return 0;
                }
            }
            Thread.Sleep(1);
        }
        if (!worker.Join(TimeSpan.FromSeconds(1))) throw new Exception("Unwitnessed blocked worker; fixture retained untouched.");
        if (failure != null) throw new Exception("Outer reader retirement failed.", failure);
        if (calls != 1) throw new Exception("Close callback must occur exactly once.");
        if (mode == "control")
        {
            if (nested != null || exposed) throw new Exception("Other-file callback control failed.", nested);
        }
        else if (nested?.GetType() != typeof(InvalidOperationException) || nested.Message != Refusal || exposed)
            throw new Exception("Expected exact refusal and native ownership unchanged.", nested);
        outer.Dispose(); data.Dispose(); log.Dispose(); complete.Dispose();
        using (var peer = new LiteDatabase(new ConnectionString { Filename = file, Password = password, Connection = ConnectionType.Shared }))
            peer.GetCollection("rows").Insert(Fixture.Row(9));
        Fixture.Verify(file, password, new[] { 1, 9 }.Concat(Enumerable.Range(100, 60)).ToArray());
        if (mode == "control") Fixture.Verify(otherFile, password, new[] { 1, 9 });
        Console.WriteLine("CHILD_FIXED_VERIFIED: callback refused/control allowed; ownership retained; later writer and two cold reopens verified.");
        return 2;
    }

    private static bool SelfWait(SharedEngine outer, Thread worker, Mutex mutex)
    {
        var core = typeof(SharedEngine).GetField("_closingCore", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(outer);
        if (core == null) return false;
        var operations = Field(core, "_operations");
        var exclusive = operations.GetType().GetField("_exclusive", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(operations);
        return ReferenceEquals(exclusive, worker) && (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0 && !Probe(mutex);
    }

    private static bool Probe(Mutex mutex)
    {
        var acquired = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { acquired = mutex.WaitOne(0); if (acquired) mutex.ReleaseMutex(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(2))) throw new Exception("Native probe worker stalled.");
        if (failure != null) throw new Exception("Native probe failed.", failure);
        return acquired;
    }

    private static object Field(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target)
        ?? throw new Exception("Required production observation unavailable: " + name);

    private static void OrdinaryRecursion(string file, string? password)
    {
        Fixture.Seed(file, password);
        using (var engine = new SharedEngine(new EngineSettings { Filename = file, Password = password }))
        {
            var calls = 0;
            IEnumerable<BsonDocument> Input()
            {
                yield return Fixture.Row(2);
                if (engine.Pragma("USER_VERSION").AsInt32 != 0) throw new Exception("Recursive getter differs.");
                calls++;
                yield return Fixture.Row(3);
            }
            engine.Insert("rows", Input(), BsonAutoId.Int32);
            if (calls != 1) throw new Exception("Ordinary recursion control not reached.");
        }
        Fixture.Verify(file, password, new[] { 1, 2, 3 });
    }
}
