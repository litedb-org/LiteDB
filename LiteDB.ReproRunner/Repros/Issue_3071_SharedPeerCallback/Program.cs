using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using LiteDB;
using LiteDB.Engine;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3071_SharedPeerCallback;

/// <summary>
/// Repro of LiteDB issue #3071. A Shared write runs its lazy input sequence while it retains the
/// database's native mutex. When the sequence synchronously writes through a second Shared
/// connection to the same file, that connection waits for the mutex forever: it is released
/// only after the sequence returns. Both routes are covered, each plain and encrypted: the
/// write owning the mutex itself (unanchored) and a thread with a leased reader pinning it.
/// A custom stream is user code as well: disposing a connection checkpoints through it while
/// the mutex is still owned, and a peer write from that stream waits the same way.
///
/// Exit 0 (bug reproduced, the known-bad package must do this): every case leaves the nested
/// call blocked with the peer observed waiting for the native mutex, not merely slow.
/// Exit 2 (fixed, the candidate must do this): every nested call is refused promptly, the
/// aborted outer write leaves no rows, both connections stay usable, and the same-connection
/// and other-database controls complete. Anything else exits 1 and satisfies neither variant.
/// </summary>
internal static class Program
{
    private const int Fixed = 2;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private enum Outcome { Blocked, Refused }

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        // Default Shared mutex names embed the escaped path, and Unix limits their length:
        // keep the files in a short temporary directory instead of the runner's deep one.
        var directory = Path.Combine(Path.GetTempPath(), "i3071-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        host.SendLog("database directory: " + directory);
        // Development prereleases refuse LiteDatabase until this risk is acknowledged; other builds lack it.
        typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")
            ?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

        try
        {
            Controls(host, directory);
            var outcomes = new List<Outcome>();
            foreach (var pinned in new[] { false, true })
            foreach (var encrypted in new[] { false, true })
            {
                var name = (pinned ? "pinned" : "unanchored") + "-" + (encrypted ? "encrypted" : "plain");
                var outcome = PeerWrite(Path.Combine(directory, name + ".db"), pinned, encrypted);
                host.SendLog($"{name}: {outcome}");
                outcomes.Add(outcome);
            }
            foreach (var encrypted in new[] { false, true })
            {
                var name = "dispose-checkpoint-" + (encrypted ? "encrypted" : "plain");
                var outcome = PeerWriteFromClose(Path.Combine(directory, name + ".db"), encrypted);
                host.SendLog($"{name}: {outcome}");
                outcomes.Add(outcome);
            }

            if (outcomes.All(x => x == Outcome.Blocked))
            {
                // The blocked workers still retain the mutexes: exit without disposing them.
                const string message = "PROVEN_BLOCK: each nested peer write waits for the native mutex its own outer operation retains.";
                Console.WriteLine(message);
                host.SendResult(true, message);
                return 0;
            }
            if (outcomes.All(x => x == Outcome.Refused))
            {
                const string message = "FIXED_VERIFIED: each nested peer write was refused promptly; state and controls verified.";
                Console.WriteLine(message);
                host.SendResult(false, message);
                return Fixed;
            }
            throw new InvalidOperationException("Mixed outcomes: " + string.Join(", ", outcomes));
        }
        catch (Exception error)
        {
            host.SendResult(false, "The repro failed.", new { Exception = error.ToString() });
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    /// <summary>Nested writes that must complete on every version: the same connection, another database.</summary>
    private static void Controls(ReproHostClient host, string directory)
    {
        var file = Path.Combine(directory, "control.db");
        var other = Path.Combine(directory, "control-other.db");
        Seed(file, null);
        Seed(other, null);
        using (var outer = Outer(file, null))
        using (var otherDb = new LiteDatabase(new SharedEngine(new EngineSettings { Filename = other })))
        {
            var error = RunBounded(() => outer.Insert("rows", Input(() =>
            {
                outer.Insert("rows", new[] { Row(9) }, BsonAutoId.Int32);
                otherDb.GetCollection("rows").Insert(Row(9));
            }), BsonAutoId.Int32), out var completed);
            if (!completed || error != null) throw new InvalidOperationException("Control failed: " + error);
        }
        Verify(file, null, 1, 2, 3, 9);
        Verify(other, null, 1, 9);
        host.SendLog("controls: same-connection and other-database nested writes completed");
    }

    private static Outcome PeerWrite(string file, bool pinned, bool encrypted)
    {
        var password = encrypted ? "secret" : null;
        Seed(file, password);
        var outer = Outer(file, password);
        var peerEngine = new SharedEngine(new EngineSettings { Filename = file, Password = password });
        var peer = new LiteDatabase(peerEngine);
        var inCallback = 0;
        Exception? nested = null;

        var error = RunBounded(() =>
        {
            // A leased reader holds no mutex; the thread's next write pins it.
            using var anchor = pinned ? outer.Query("rows", new Query()) : null;
            if (anchor != null && !anchor.Read()) throw new InvalidOperationException("Missing anchor row.");
            outer.Insert("rows", Input(() =>
            {
                Volatile.Write(ref inCallback, 1);
                try { peer.GetCollection("rows").Insert(Row(9)); }
                catch (Exception ex) { nested = ex; throw; }
                finally { Volatile.Write(ref inCallback, 0); }
            }), BsonAutoId.Int32);
        }, out var completed);

        if (!completed)
        {
            // Not only slow: the worker is still inside its callback and the peer is queued
            // for the native mutex. Nothing is disposed; the graph is live.
            if (Volatile.Read(ref inCallback) != 1 || MutexWaiters(peerEngine) < 1)
                throw new InvalidOperationException("Timed out without the peer waiting for the native mutex.");
            return Outcome.Blocked;
        }

        if (!(nested is InvalidOperationException) || !ReferenceEquals(error, nested))
            throw new InvalidOperationException("Expected the outer write to fail with the nested refusal, got " + (error ?? nested));
        // Neither connection is left unusable, and the aborted write left nothing behind.
        peer.GetCollection("rows").Insert(Row(9));
        outer.Insert("rows", new[] { Row(4) }, BsonAutoId.Int32);
        peer.Dispose();
        outer.Dispose();
        Verify(file, password, 1, 4, 9);
        return Outcome.Refused;
    }

    /// <summary>
    /// Disposing a connection checkpoints its WAL through the caller's data stream while the
    /// connection still owns the mutex. The stream's write calls the peer.
    /// </summary>
    private static Outcome PeerWriteFromClose(string file, bool encrypted)
    {
        var password = encrypted ? "secret" : null;
        Seed(file, password);
        var data = new CallbackFile(file);
        // The WAL beside the data file, as a peer opening the file by name finds it.
        var log = new CallbackFile(Path.Combine(Path.GetDirectoryName(file)!,
            Path.GetFileNameWithoutExtension(file) + "-log" + Path.GetExtension(file)));
        var outer = new SharedEngine(new EngineSettings { Filename = file, Password = password, DataStream = data, LogStream = log });
        var peerEngine = new SharedEngine(new EngineSettings { Filename = file, Password = password });
        var peer = new LiteDatabase(peerEngine);
        var inCallback = 0;
        Exception? nested = null;

        var error = RunBounded(() =>
        {
            outer.Insert("rows", new[] { Row(100) }, BsonAutoId.Int32);
            data.Arm(() =>
            {
                Volatile.Write(ref inCallback, 1);
                try { peer.GetCollection("rows").Insert(Row(9)); }
                catch (Exception ex) { nested = ex; }
                finally { Volatile.Write(ref inCallback, 0); }
            });
            outer.Dispose();
        }, out var completed);

        if (!completed)
        {
            if (Volatile.Read(ref inCallback) != 1 || MutexWaiters(peerEngine) < 1)
                throw new InvalidOperationException("Timed out without the peer waiting for the native mutex.");
            return Outcome.Blocked;
        }

        if (error != null || !(nested is InvalidOperationException))
            throw new InvalidOperationException("Expected the dispose to finish with the stream's peer write refused, got " + (error ?? nested));
        data.Dispose();
        log.Dispose();
        peer.GetCollection("rows").Insert(Row(9));
        peer.Dispose();
        Verify(file, password, 1, 9, 100);
        return Outcome.Refused;
    }

    /// <summary>The database's own file, shared with the peer, calling back once on its next write.</summary>
    private sealed class CallbackFile : FileStream
    {
        private Action? _onWrite;

        internal CallbackFile(string path)
            : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)
        {
        }

        internal void Arm(Action action) => Volatile.Write(ref _onWrite, action);

        public override void Write(byte[] array, int offset, int count)
        {
            Interlocked.Exchange(ref _onWrite, null)?.Invoke();
            base.Write(array, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Interlocked.Exchange(ref _onWrite, null)?.Invoke();
            base.Write(buffer);
        }
    }

    private static SharedEngine Outer(string file, string? password) =>
        // Any user callback makes the write's ownership outlive a scoped call; the identity is enough.
        new SharedEngine(new EngineSettings { Filename = file, Password = password, ReadTransform = (_, value) => value });

    private static IEnumerable<BsonDocument> Input(Action callback)
    {
        yield return Row(2);
        callback();
        yield return Row(3);
    }

    private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id };

    private static Exception? RunBounded(Action action, out bool completed)
    {
        Exception? error = null;
        var worker = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        var started = Stopwatch.StartNew();
        worker.Start();
        completed = worker.Join(Bound);
        Console.WriteLine($"worker completed={completed} after {started.ElapsedMilliseconds} ms");
        return error;
    }

    /// <summary>Threads of the connection queued for its native mutex (a private counter of every version under test).</summary>
    private static int MutexWaiters(SharedEngine engine)
    {
        var field = typeof(SharedEngine).GetField("_mutexWaiters", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SharedEngine._mutexWaiters is missing; the wait cannot be observed.");
        return (int)field.GetValue(engine)!;
    }

    private static void Seed(string file, string? password)
    {
        using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password, Connection = ConnectionType.Direct });
        db.GetCollection("rows").Insert(Row(1));
        db.GetCollection("rows").EnsureIndex("value");
        db.GetCollection("sentinel").Insert(Row(42));
    }

    private static void Verify(string file, string? password, params int[] ids)
    {
        using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password, Connection = ConnectionType.Direct });
        var rows = db.GetCollection("rows");
        var actual = rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
        if (!actual.SequenceEqual(ids)) throw new InvalidOperationException($"Rows [{string.Join(",", actual)}], expected [{string.Join(",", ids)}].");
        foreach (var id in ids)
            if (rows.FindOne(Query.EQ("value", id))?["_id"].AsInt32 != id) throw new InvalidOperationException($"Index lookup of {id} failed.");
        if (db.GetCollection("sentinel").FindById(42)?["value"].AsInt32 != 42) throw new InvalidOperationException("Sentinel changed.");
    }
}
