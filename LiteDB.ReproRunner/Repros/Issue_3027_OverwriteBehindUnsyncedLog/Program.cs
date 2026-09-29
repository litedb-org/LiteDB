using System.Reflection;
using LiteDB;
using LiteDB.Engine;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3027_OverwriteBehindUnsyncedLog;

/// <summary>
/// Defect fixed by PR #3027 (guard: OverwriteBarrier_Tests). A checkpoint overwrites the data file in
/// place behind a header journal and the WAL, its recovery copy. With "durable commits=false" on storage
/// whose log cannot sync (#2242: the sync request is refused, EINVAL), the known-bad engine went ahead
/// with that recovery copy only in the operating system's cache: a power loss mid-overwrite tore the data
/// file, with nothing on the device to repair it from. Fixed (decision D, implementation note 12 of
/// docs/decisions/durability-policy.md): the checkpoint refuses before it writes the journal. It writes
/// nothing, keeps the WAL, and a checkpoint drains the WAL once the log syncs; opting out gives up recent
/// commits, never the data file's integrity.
///
/// Black box: the database runs on two caller streams (EngineSettings.DataStream/LogStream), each a
/// FileStream subclass modeling a device. Its Flush(true) is the engine's device sync on both versions;
/// the data file's always succeeds, the log's answers EINVAL ("cannot sync") once armed. Each file keeps
/// a device image, updated only by a successful sync; the writes since then are pending, in order. A power
/// loss during the checkpoint's overwrite leaves the data file with a prefix of its pending writes (the
/// last one whole, or torn in half) and the log as of its last successful sync.
///
/// Exit code 0: the defect reproduced (the known-bad LiteDB must do this). Exit code 1: the fixed
/// behavior was verified in full (the candidate must do this). Exit code 2: anything else.
/// </summary>
internal static class Program
{
    private const int Reproduced = 0;
    private const int Fixed = 1;
    private const int Inconclusive = 2;
    private const int Synced = 10;
    private const int Rows = 30;

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var directory = Path.Combine(context.SharedDatabaseRoot ?? Path.GetTempPath(), "Issue_3027_OverwriteBehindUnsyncedLog-" + Guid.NewGuid().ToString("N"));
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
        using var data = new DeviceFile(Path.Combine(directory, "rows.db"));
        using var log = new DeviceFile(Path.Combine(directory, "rows-log.db"));

        // Rows 1..10 and the value index, checkpointed into the data file and synced (both files sync).
        using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }))
        using (var db = new LiteDatabase(engine, disposeOnClose: false))
        {
            db.GetCollection("rows").EnsureIndex("value");
            db.GetCollection("rows").Insert(Enumerable.Range(1, Synced).Select(Row));
            db.Checkpoint();
        }
        var setup = Inspect(Device(data, log));
        Require(setup.Intact && setup.Ids.Count == Synced, $"after the setup a power loss leaves {setup.Detail}, expected rows 1-{Synced}");
        var dataBefore = (Live: data.Live, Device: data.Device);
        host.SendLog($"Setup: rows 1-{Synced} are in the data file on the device ({dataBefore.Device.Length} bytes); the log now answers every sync with EINVAL");

        log.CannotSync = true;
        var settings = new EngineSettings { DataStream = data, LogStream = log, DurableCommits = false };
        var engineOpen = new LiteEngine(settings);
        var dbOpen = new LiteDatabase(engineOpen, disposeOnClose: false);
        var open = true;
        try
        {
            dbOpen.CheckpointSize = 0; // only the explicit checkpoint below
            var rows = dbOpen.GetCollection("rows");
            for (var id = Synced + 1; id <= Rows; id++) rows.Insert(Row(id));
            var wal = log.Live;
            var refused = log.Refused;
            var dataWrites = data.Operations;

            // A power loss during the checkpoint: capture what the devices hold when the data file is next synced
            // with writes of the checkpoint pending (the known-bad engine syncs it after its overwrite).
            Capture? capture = null;
            data.BeforeSync = () => { if (capture == null && data.PendingSince(dataWrites) > 0) capture = Capture.Take(data, log, dataBefore.Device.Length); };
            var pages = engineOpen.Checkpoint();
            data.BeforeSync = null;
            if (capture == null && data.PendingSince(dataWrites) > 0) capture = Capture.Take(data, log, dataBefore.Device.Length);
            host.SendLog($"Checkpoint with durable commits=false: {pages} pages; the log refused {log.Refused - refused} syncs; " +
                $"{data.Operations - dataWrites} writes to the data file");

            if (capture != null) return Torn(host, capture, pages, log.Refused - refused);
            host.SendLog($"The WAL is kept: {wal.Length} bytes in the OS cache, {log.Device.Length} bytes on the device");

            // Fixed: nothing was written to the data file and the WAL is kept as it was, without a journal.
            Require(pages == 0, $"the checkpoint returned {pages} pages but wrote nothing");
            Require(log.Refused > refused, "the checkpoint never asked the log to sync");
            Require(data.Operations == dataWrites && data.Live.SequenceEqual(dataBefore.Live) && data.Device.SequenceEqual(dataBefore.Device),
                "the data file changed since the setup");
            Require(log.Live.SequenceEqual(wal), "the checkpoint changed the WAL");
            RequireRows(dbOpen, Rows, "the engine after the refused checkpoint");
            var info = dbOpen.GetCollection("$database").FindAll().Single();
            Require(info["writeFailure"].IsNull && !info["readOnly"].AsBoolean, $"the refusal was recorded as a failure: {info}");

            var lost = Inspect(Device(data, log));
            Require(lost.Intact && lost.Ids.Count == Synced, $"a power loss after the refused checkpoint leaves {lost.Detail}, expected rows 1-{Synced}");

            open = false;
            dbOpen.Dispose();
            engineOpen.Dispose();
            Require(data.Operations == dataWrites && data.Device.SequenceEqual(dataBefore.Device), "the close checkpoint wrote to the data file");
            var cached = Inspect((data.Live, log.Live));
            Require(cached.Intact && cached.Ids.Count == Rows, $"the files a process crash leaves hold {cached.Detail}, expected rows 1-{Rows}");

            // Once the log syncs, a checkpoint drains the WAL into the data file.
            log.CannotSync = false;
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Require(engine.Checkpoint() > 0, "a checkpoint on a log that syncs again wrote nothing");
                RequireRows(db, Rows, "the engine after the log synced again");
            }
            var drained = Inspect(Device(data, log));
            Require(drained.Intact && drained.Ids.Count == Rows, $"a power loss after the log synced again and a checkpoint leaves {drained.Detail}, expected rows 1-{Rows}");

            return (Fixed, "FIXED: with durable commits=false on a log that cannot sync (EINVAL), the checkpoint wrote nothing to the data file " +
                $"and kept the WAL: a power loss keeps rows 1-{Synced} of the data file intact (rows {Synced + 1}-{Rows} of the unsynced WAL are lost, " +
                "a process crash keeps them), and once the log syncs a checkpoint drains the WAL");
        }
        finally
        {
            if (open)
            {
                dbOpen.Dispose();
                engineOpen.Dispose();
            }
        }
    }

    /// <summary>The known-bad outcome: the checkpoint overwrote the data file; a power loss mid-overwrite tears it.</summary>
    private static (int Code, string Summary) Torn(ReproHostClient host, Capture capture, int pages, int refused)
    {
        Require(refused > 0, "the checkpoint overwrote the data file without asking the log to sync");
        Require(capture.LogPending > 0, "the log held no pending write (journal or WAL) when the data file was overwritten");
        Require(capture.Overwrites > 0, "the checkpoint only appended to the data file");

        host.SendLog($"At the data sync after the overwrite the log held {capture.LogPending} writes (journal and WAL) in the OS cache only; " +
            $"its device image is {capture.Log.Length} bytes");
        var torn = new List<string>();
        foreach (var (name, image) in capture.Images)
        {
            var outcome = Inspect((image, capture.Log));
            host.SendLog($"Power loss {name}: {(outcome.Intact ? "intact, " : "")}{outcome.Detail}");
            if (!outcome.Intact) torn.Add($"{name}: {outcome.Detail}");
        }
        if (torn.Count == 0)
            throw new InvalidOperationException($"every one of the {capture.Images.Count} power-loss images of the checkpoint's overwrite reopens intact");

        return (Reproduced, $"REPRODUCED: with durable commits=false on a log that cannot sync (EINVAL), a checkpoint ({pages} pages) overwrote the data file " +
            $"in place behind a header journal and WAL only in the OS cache: {capture.Overwrites} of its {capture.Writes} writes overwrote bytes " +
            $"the device held, and {torn.Count} of the {capture.Images.Count} images a power loss mid-overwrite leaves do not hold rows 1-{Synced} " +
            $"intact (first: {torn[0]})");
    }

    /// <summary>What a power loss leaves now: each file as of its last successful sync.</summary>
    private static (byte[] Data, byte[] Log) Device(DeviceFile data, DeviceFile log) => (data.Device, log.Device);

    private sealed record Outcome(bool Intact, List<int> Ids, string Detail);

    /// <summary>
    /// Open a copy of the files and read "rows". Intact: rows 1..n (n at least 10) each byte for byte, the value
    /// index finding each and the count agreeing (rows after 10 were commits the WAL may lose whole).
    /// </summary>
    private static Outcome Inspect((byte[] Data, byte[] Log) image)
    {
        try
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            data.Write(image.Data);
            log.Write(image.Log);
            data.Position = log.Position = 0;
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            return Check(db);
        }
        catch (Exception error)
        {
            return new Outcome(false, new List<int>(), $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>Read "rows" as <see cref="Inspect"/> does; a read that throws propagates.</summary>
    private static Outcome Check(LiteDatabase db)
    {
        var rows = db.GetCollection("rows");
        var all = rows.FindAll().OrderBy(x => x["_id"].AsInt32).ToList();
        var ids = all.Select(x => x["_id"].AsInt32).ToList();
        if (ids.Count < Synced || !ids.SequenceEqual(Enumerable.Range(1, ids.Count))) return new Outcome(false, ids, $"rows [{Ids(ids)}]");
        var changed = all.FirstOrDefault(x => !BsonSerializer.Serialize(x).SequenceEqual(BsonSerializer.Serialize(Row(x["_id"].AsInt32))));
        if (changed != null) return new Outcome(false, ids, $"rows [{Ids(ids)}], _id {changed["_id"]} changed");
        for (var value = 0; value < 5; value++)
        {
            var found = rows.Find(Query.EQ("value", value)).Select(x => x["_id"].AsInt32).OrderBy(x => x).ToList();
            if (!found.SequenceEqual(ids.Where(id => id % 5 == value)))
                return new Outcome(false, ids, $"rows [{Ids(ids)}], the value index finds [{Ids(found)}] for value {value}");
        }
        var count = rows.Count();
        return count == ids.Count ? new Outcome(true, ids, $"rows [{Ids(ids)}]") : new Outcome(false, ids, $"rows [{Ids(ids)}], counted {count}");
    }

    private static void RequireRows(LiteDatabase db, int count, string name)
    {
        var outcome = Check(db);
        Require(outcome.Intact && outcome.Ids.Count == count, $"{name} reads {outcome.Detail}, expected rows 1-{count}");
    }

    private static string Ids(IEnumerable<int> ids)
    {
        var sorted = ids.OrderBy(x => x).ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j - i >= 2 ? $"{sorted[i]}-{sorted[j]}" : string.Join(", ", sorted.Skip(i).Take(j - i + 1)));
            i = j + 1;
        }
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static BsonDocument Row(int id) => new BsonDocument
    {
        ["_id"] = id, ["value"] = id % 5, ["payload"] = new string((char)('a' + id % 26), 1500) + id
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// The devices when the data file was about to be synced with the checkpoint's writes pending: every data
    /// image a power loss before that sync may leave (a prefix of the pending writes, the last whole or torn),
    /// and the log as of its last successful sync.
    /// </summary>
    private sealed record Capture(List<(string Name, byte[] Image)> Images, byte[] Log, int LogPending, int Writes, int Overwrites)
    {
        internal static Capture Take(DeviceFile data, DeviceFile log, long deviceLength)
        {
            var (writes, overwrites) = data.PendingWrites(deviceLength);
            return new Capture(data.PowerLossImages(), log.Device, log.Pending, writes, overwrites);
        }
    }
}
