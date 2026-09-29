using System.IO.Compression;
using System.Reflection;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3027_LegacyWalConversion;

/// <summary>
/// Regression since 5.0.21 fixed by PR #3027 (guard: LegacyWalSharedMigration_Tests). The first
/// writable open converts a 5.0.21 file: it checkpointed the legacy WAL and then cleared it, assuming
/// the checkpoint had drained it. The checkpoint became lease-aware for shared readers: while a reader
/// holds a snapshot (a live lease file in "&lt;file&gt;-readers") it backfills only up to that snapshot,
/// or nothing when the registry cannot be read. The conversion then truncated the legacy WAL anyway,
/// discarding every transaction 5.0.21 had committed there, and converted the file, so 5.0.21 could
/// not open it any more either. Fixed: the conversion drains the legacy WAL completely or refuses the
/// open (LOCK_TIMEOUT) without changing either file.
///
/// WalCrash_5_0_21.zip (LiteDB-Artifacts, pinned in LiteDB.Tests/Resources/artifacts.json) is a process-crash image written by the LiteDB 5.0.21
/// package: 100 documents {_id, value: 0} checkpointed, then 20 updates to value 7 and one insert
/// (_id 100) committed to the WAL only. 5.0.21 recovers all of them (101 documents, 21 with value 7).
///
/// Exit code 0: the defect reproduced (the known-bad LiteDB must do this). Exit code 1: the fixed
/// behavior was verified in full (the candidate must do this). Exit code 2: anything else.
/// </summary>
internal static class Program
{
    private const int Reproduced = 0;
    private const int Fixed = 1;
    private const int Inconclusive = 2;

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        // A short path: shared mode on Unix names its mutex after the escaped full path, and the known-bad
        // state throws on a long one (a separate regression, SharedMutexNameLength_Tests), not this defect.
        var directory = Path.Combine(Path.GetTempPath(), "rr3027-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
        var data = Path.Combine(directory, "crash.db");
        var log = Path.Combine(directory, "crash-log.db");
        File.WriteAllBytes(data, Fixture("WalCrash_5_0_21.zip", "crash.db"));
        File.WriteAllBytes(log, Fixture("WalCrash_5_0_21.zip", "crash-log.db"));
        var (originalData, originalLog) = (File.ReadAllBytes(data), File.ReadAllBytes(log));
        Require(originalData[59] == 8, "crash.db is not a 5.0.21 (file version 8) file");
        var connection = $"Filename={data};Connection=shared";

        // Another process's shared reader holds a snapshot: its lease file is open and locked.
        var lease = Path.Combine(Directory.CreateDirectory(data + "-readers").FullName, "1-live.lease");
        Exception? refusal = null;
        string during;
        using (new FileStream(lease, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            try
            {
                using var db = new LiteDatabase(connection);
                during = Describe(db);
            }
            catch (LiteException error) when (error.ErrorCode == LiteException.LOCK_TIMEOUT)
            {
                refusal = error;
                during = $"refused: LiteException {error.ErrorCode}: {error.Message}";
            }
        }
        // An engine that drained the log may have deleted it on close (5.0.21 does).
        var (dataAfter, logAfter) = (File.ReadAllBytes(data), File.Exists(log) ? File.ReadAllBytes(log) : Array.Empty<byte>());
        host.SendLog($"Shared open while the reader held its lease: {during}; data file version {originalData[59]} -> {dataAfter[59]}, " +
            $"legacy WAL {originalLog.Length} -> {logAfter.Length} bytes");

        using var reopened = new LiteDatabase(connection);
        var after = Describe(reopened);
        var (count, sevens) = Count(reopened);
        host.SendLog($"Shared open after the reader closed: {after}");

        if (refusal == null && (count, sevens) != (101, 21))
        {
            return (Reproduced, $"REPRODUCED: the conversion ran while a shared reader held a snapshot and discarded the 5.0.21 WAL " +
                $"({originalLog.Length} -> {logAfter.Length} bytes): {count} documents, {sevens} with value 7 remain of 101 and 21");
        }

        Require(refusal != null, $"the shared open succeeded while the reader held its lease ({during}) and every commit was kept");
        Require(dataAfter.AsSpan().SequenceEqual(originalData) && logAfter.AsSpan().SequenceEqual(originalLog),
            "the refused conversion changed the data file or the legacy WAL");
        Require((count, sevens) == (101, 21), $"after the reader closed: {after}, expected 101 documents, 21 with value 7");
        Require(File.ReadAllBytes(data)[59] > 8, "the open after the reader closed did not convert the file");
        return (Fixed, "FIXED: the conversion was refused with LOCK_TIMEOUT while a shared reader held a snapshot, both files unchanged; " +
            "once the reader closed it converted the file with every 5.0.21 commit (101 documents, 21 with value 7)");
    }

    private static string Describe(LiteDatabase db)
    {
        var (count, sevens) = Count(db);
        return $"{count} documents, {sevens} with value 7";
    }

    private static (int Count, int Sevens) Count(LiteDatabase db)
    {
        var docs = db.GetCollection("docs").FindAll().ToList();
        return (docs.Count, docs.Count(x => x["value"] == 7));
    }

    private static byte[] Fixture(string archive, string entry)
    {
        // Pinned in LiteDB.Tests/Resources/artifacts.json; LITEDB_ARTIFACTS_DIR overrides the download.
        using var zip = new ZipArchive(File.OpenRead(LiteDB.Tests.ArtifactFixtures.Path(archive)), ZipArchiveMode.Read);
        using var source = (zip.GetEntry(entry) ?? throw new InvalidOperationException($"{archive} has no {entry}")).Open();
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
