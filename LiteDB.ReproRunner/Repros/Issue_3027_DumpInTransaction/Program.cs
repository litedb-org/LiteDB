using System.Reflection;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3027_DumpInTransaction;

/// <summary>
/// Regression since 5.0.21 fixed by PR #3027 (guard: DumpPinnedWalSlot_Tests). A transaction larger than
/// its page limit safepoints its dirty pages into unconfirmed WAL slots, and its next safepoint or its
/// commit rewrites such a page in place at its previous slot, which requires the cached frame to be idle.
/// <c>$dump</c> and <c>$page_list</c> run in the thread's explicit transaction with a read snapshot that
/// reads the transaction's own safepointed pages from those slots and keeps them pinned until the
/// transaction ends. The next rewrite then failed an ENSURE (INVALID_DATAFILE_STATE, "only idle readable
/// pages can be evicted"), which stopped the engine and marked the data file invalid; the transaction was
/// lost. 5.0.21 appended every WAL page, so a pinned slot stayed valid and the commit succeeded. Fixed: a
/// pinned slot is kept and the new version appended.
///
/// The repro reads every page with <c>$dump(pageID)</c> (or <c>$page_list(pageID)</c>, as the guard does
/// for the transaction's dirty pages) inside a transaction of 1000 inserts with Transaction Pages=50,
/// updates the 1000 rows and commits.
///
/// Exit code 0: the defect reproduced (the known-bad LiteDB must do this). Exit code 1: the fixed
/// behavior was verified in full (the candidate must do this). Exit code 2: anything else.
/// </summary>
internal static class Program
{
    private const int Reproduced = 0;
    private const int Fixed = 1;
    private const int Inconclusive = 2;
    private const int Documents = 1000;
    private const int InvalidDatafileStateFlag = 191; // HeaderPage.P_INVALID_DATAFILE_STATE
    private const string Ensure = "only idle readable pages can be evicted";
    private const string Intact = "found 1001 documents, 1000 updated";

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var directory = Path.Combine(context.SharedDatabaseRoot ?? Path.GetTempPath(), "Issue_3027_DumpInTransaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var engine = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {engine.Location}");
        // A development prerelease build (LITEDB_PREDEV) refuses to open files until this is acknowledged.
        engine.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

        try
        {
            var (code, summary) = Run(host, directory);
            host.SendResult(code == Reproduced, summary);
            return code;
        }
        catch (Exception error)
        {
            host.SendResult(false, $"INCONCLUSIVE: {error.GetType().Name}: {error.Message}", new { Exception = error.ToString() });
            Console.Error.WriteLine(error);
            return Inconclusive;
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static (int Code, string Summary) Run(ReproHostClient host, string directory)
    {
        var results = new[] { "$dump", "$page_list" }
            .Select(source => Scenario(host, Path.Combine(directory, source.TrimStart('$') + ".db"), source))
            .ToArray();

        if (results.All(x => x.Ensure))
        {
            var invalid = results.All(x => x.Invalid) ? " and marked the data file invalid" : "";
            return (Reproduced, "REPRODUCED: after $dump or $page_list read pages that an explicit transaction had safepointed into the WAL, " +
                $"its next safepoint failed an ENSURE (LiteException 999: {Ensure}), which stopped the engine{invalid} and lost the transaction (" +
                string.Join("; ", results.Select(x => $"{x.Failure}, then {x.Stopped}, a reopen {x.Reopen}")) + ")");
        }

        foreach (var result in results)
        {
            Require(result.Failure == null, $"the transaction with {result.Source} failed, but not with the ENSURE of the defect: {result.Failure}");
            Require(result.Reopen == Intact, $"after the commit with {result.Source}, a reopen {result.Reopen}");
            Require(!result.Invalid, $"the commit with {result.Source} marked the data file invalid");
        }
        return (Fixed, "FIXED: after $dump or $page_list read pages that an explicit transaction had safepointed into the WAL, its " +
            $"safepoints and its commit succeeded, and a reopen found every row of it ({Documents + 1} documents, {Documents} updated)");
    }

    private sealed record Outcome(string Source, string? Failure, bool Ensure, string Stopped, bool Invalid, string Reopen);

    private static Outcome Scenario(ReproHostClient host, string path, string source)
    {
        var x = new string('x', 500);
        var y = new string('y', 500);
        string? failure = null;
        var ensure = false;
        var stopped = "the engine kept working";
        // A transaction page limit of 50 makes the transaction safepoint into unconfirmed WAL slots.
        var db = new LiteDatabase($"Filename={path};Transaction Pages=50");
        try
        {
            var col = db.GetCollection("docs");
            col.Insert(new BsonDocument { ["_id"] = -1, ["payload"] = "committed" });
            Require(db.BeginTrans(), "BeginTrans did not start a transaction");
            col.Insert(Enumerable.Range(0, Documents).Select(i => new BsonDocument { ["_id"] = i, ["payload"] = x }));

            // As the guard does for the transaction's dirty pages, here for every page: one query per page.
            var lastPageID = db.GetCollection("$database").FindAll().Single()["lastPageID"].AsInt32;
            var step = $"reading {source}";
            try
            {
                var (rows, fromLog) = (0, 0);
                for (var pageID = 1; pageID <= lastPageID; pageID++)
                {
                    step = $"reading {source}({pageID})";
                    var read = db.Execute($"SELECT $ FROM {source}({pageID})").ToEnumerable().ToList();
                    rows += read.Count;
                    fromLog += read.Count(r => r["_origin"] == "Log");
                }
                host.SendLog($"{source}(1..{lastPageID}) inside the transaction returned {rows} rows" +
                    (source == "$dump" ? $", {fromLog} of them pages the transaction had safepointed into the WAL" : ""));
                Require(rows >= lastPageID, $"{source} returned {rows} rows for {lastPageID} pages");
                Require(source != "$dump" || fromLog > 0, "the transaction safepointed no page into the WAL before $dump read it");

                step = "the update after it";
                col.Update(Enumerable.Range(0, Documents).Select(i => new BsonDocument { ["_id"] = i, ["payload"] = y }));
                step = "the commit";
                Require(db.Commit(), "Commit reported no transaction");
            }
            catch (LiteException error)
            {
                failure = $"{step} failed with LiteException {error.ErrorCode}: {error.Message}";
                ensure = error.ErrorCode == LiteException.INVALID_DATAFILE_STATE && error.Message.Contains(Ensure);
                try
                {
                    col.Count();
                }
                catch (Exception next)
                {
                    stopped = next is LiteException { ErrorCode: LiteException.INVALID_DATAFILE_STATE } && next.Message.Contains(Ensure)
                        ? "the engine stopped" : $"the next read threw {next.GetType().Name}: {next.Message}";
                }
            }
        }
        finally
        {
            try { db.Dispose(); }
            catch (Exception error) { host.SendLog($"Dispose after {source} threw {error.GetType().Name}: {error.Message}"); }
        }
        host.SendLog($"Transaction with {source}: {failure ?? "committed"}" + (failure != null ? $"; {stopped}" : ""));

        var invalid = File.ReadAllBytes(path)[InvalidDatafileStateFlag] != 0;
        string reopen;
        try
        {
            using var reopened = new LiteDatabase(path);
            var col = reopened.GetCollection("docs");
            var committed = col.FindById(-1);
            reopen = committed == null || committed["payload"] != "committed"
                ? "lost the row committed before the transaction"
                : $"found {col.Count()} documents, {col.Count(Query.EQ("payload", y))} updated";
        }
        catch (Exception error)
        {
            reopen = $"threw {error.GetType().Name}: {error.Message}";
        }
        host.SendLog($"Data file header {(invalid ? "marked invalid" : "not marked invalid")}; reopen after {source}: {reopen}");
        return new Outcome(source, failure, ensure, stopped, invalid, reopen);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
