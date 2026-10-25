using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3092_ReadSnapshotReusedPage;

/// <summary>
/// Repro of LiteDB issue #3092. Inside a legacy BeginTrans transaction (Direct mode,
/// TransactionPageLimit = 1) a read of collection "source" through index "s" pins a read
/// snapshot. Another thread drops the index and commits, so its pages go to the free list. The
/// transaction then inserts into "target", reuses a freed page ID for a Data page, and a
/// safepoint records it as dirty. Querying "source" through "s" again must still read the
/// pinned version; the known-bad build resolves the reused ID to the transaction's own "target"
/// page instead (LiteException 999 "page type must be index page", another exception, or a
/// wrong count).
///
/// Exit 0 (bug reproduced, the known-bad package must do this): in every case, plain and
/// encrypted, a query of the pinned snapshot failed or returned a wrong count after the page
/// was reused. Exit 2 (fixed, the candidate must do this): in every case every key returns its
/// full count, the transaction commits, a freed index page really was reused by "target", and
/// both collections hold their rows afterwards. Anything else exits 1 and satisfies neither.
/// </summary>
internal static class Program
{
    private const int Fixed = 2;
    private const int Rows = 600;
    private const int Keys = 10;
    private const int Expected = Rows / Keys;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private enum Outcome { Reproduced, Fixed, Inconclusive }

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var directory = context.SharedDatabaseRoot
            ?? Path.Combine(Path.GetTempPath(), "Issue_3092_ReadSnapshotReusedPage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        host.SendLog("database directory: " + directory);
        // Development prereleases refuse LiteDatabase until this risk is acknowledged; other builds lack it.
        typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")
            ?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

        try
        {
            var outcomes = new List<Outcome>();
            foreach (var encrypted in new[] { false, true })
            {
                var name = encrypted ? "encrypted" : "plain";
                var (outcome, detail) = Run(Path.Combine(directory, name + ".db"), encrypted);
                var line = $"{name}: {outcome} - {detail}";
                Console.WriteLine(line);
                host.SendLog(line);
                outcomes.Add(outcome);
            }

            if (outcomes.All(x => x == Outcome.Reproduced))
            {
                const string message = "REPRODUCED: the pinned read snapshot read the page its transaction reused for another collection.";
                Console.WriteLine(message);
                host.SendResult(true, message);
                return 0;
            }
            if (outcomes.All(x => x == Outcome.Fixed))
            {
                const string message = "FIXED_VERIFIED: the pinned read snapshot kept its version after the page was reused; counts, commit and reuse verified.";
                Console.WriteLine(message);
                host.SendResult(false, message);
                return Fixed;
            }
            const string mixed = "INCONCLUSIVE: the cases neither all reproduced nor all verified the fix.";
            Console.WriteLine(mixed);
            host.SendResult(false, mixed);
            return 1;
        }
        catch (Exception error)
        {
            host.SendResult(false, "The repro failed.", new { Exception = error.ToString() });
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static (Outcome, string) Run(string file, bool encrypted)
    {
        var db = new LiteDatabase(new ConnectionString
        {
            Filename = file, Connection = ConnectionType.Direct,
            Password = encrypted ? "secret" : null, TransactionPageLimit = 1
        });
        var reproduced = false;
        try
        {
            Seed(db);

            Exception? setupFailure = null;
            string? bug = null;
            using var pinned = new ManualResetEventSlim();
            using var dropped = new ManualResetEventSlim();
            var owner = new Thread(() =>
            {
                var stage = "begin";
                try
                {
                    if (!db.BeginTrans()) throw new InvalidOperationException("BeginTrans returned false");
                    var source = db.GetCollection("source");
                    stage = "first read";
                    var first = source.Count(Query.EQ("s", Key(3)));
                    if (first != Expected) throw new InvalidOperationException($"first read counted {first}, expected {Expected}");
                    pinned.Set();
                    if (!dropped.Wait(Bound)) throw new TimeoutException("the peer did not drop the index");

                    stage = "reuse insert";
                    var inserted = db.GetCollection("target").Insert(Filler());
                    if (inserted != 40) throw new InvalidOperationException($"inserted {inserted} rows, expected 40");

                    // From here on, a failure or a wrong count is the bug: the snapshot of "source"
                    // pinned by the first read must still reach index "s".
                    stage = "pinned read";
                    for (var key = 0; key < Keys; key++)
                    {
                        var count = source.Count(Query.EQ("s", Key(key)));
                        if (count != Expected && bug == null) bug = $"key {key} counted {count}, expected {Expected}";
                    }
                    if (bug != null) return;
                    stage = "commit";
                    if (!db.Commit()) throw new InvalidOperationException("Commit returned false");
                }
                catch (Exception error) when (stage == "pinned read")
                {
                    bug = error.GetType().Name + ": " + error.Message;
                }
                catch (Exception error)
                {
                    setupFailure = new InvalidOperationException("owner failed at " + stage, error);
                }
                finally
                {
                    pinned.Set();
                    if (bug != null || setupFailure != null)
                    {
                        try { db.Rollback(); } catch { }
                    }
                }
            }) { IsBackground = true };
            owner.Start();
            if (!pinned.Wait(Bound)) return (Outcome.Inconclusive, "the transaction did not pin its snapshot");

            int[] freed;
            try { freed = setupFailure == null ? FreeIndexPages(db) : new int[0]; }
            finally { dropped.Set(); }
            if (!owner.Join(Bound)) return (Outcome.Inconclusive, "the transaction did not finish");

            if (setupFailure != null) return (Outcome.Inconclusive, setupFailure.ToString());
            if (freed.Length == 0) return (Outcome.Inconclusive, "dropping the index freed no pages");
            if (bug != null)
            {
                reproduced = true;
                return (Outcome.Reproduced, bug);
            }

            var reused = Pages(db, "target", "Data").Concat(Pages(db, "target", "Index")).Intersect(freed).ToArray();
            if (reused.Length == 0) return (Outcome.Inconclusive, "the transaction reused none of the freed pages");
            var targets = db.GetCollection("target").Count();
            var sources = db.GetCollection("source").Count();
            if (targets != 41 || sources != Rows)
                return (Outcome.Inconclusive, $"after commit target has {targets} rows (41 expected), source {sources} ({Rows} expected)");
            return (Outcome.Fixed, $"every key counted {Expected} after page(s) {string.Join(",", reused)} were reused by target");
        }
        finally
        {
            // The known-bad engine may stop after the bug; disposing it is then best effort.
            try { db.Dispose(); } catch when (reproduced) { }
        }
    }

    private static void Seed(LiteDatabase db)
    {
        var source = db.GetCollection("source");
        source.EnsureIndex("s", "$.s");
        source.Insert(Enumerable.Range(1, Rows).Select(i => new BsonDocument
        {
            ["_id"] = i, ["s"] = Key(i % Keys), ["v"] = i % 7
        }));
        db.GetCollection("target").Insert(new BsonDocument { ["_id"] = 0 });
        db.Checkpoint();
    }

    private static BsonDocument[] Filler() => Enumerable.Range(1, 40)
        .Select(i => new BsonDocument { ["_id"] = i, ["payload"] = new string((char)('a' + i % 26), 1500) })
        .ToArray();

    private static string Key(int key) => "key-" + key + new string('x', 40);

    private static int[] Pages(LiteDatabase db, string collection, string type) =>
        db.Execute("SELECT $ FROM $dump").ToEnumerable()
            .Where(x => x["collection"].AsString == collection && x["pageType"].AsString == type)
            .Select(x => x["pageID"].AsInt32).ToArray();

    private static int[] FreeIndexPages(LiteDatabase db)
    {
        var before = Pages(db, "source", "Index");
        if (!db.GetCollection("source").DropIndex("s")) throw new InvalidOperationException("DropIndex returned false");
        return before.Except(Pages(db, "source", "Index")).ToArray();
    }
}
