using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3027_ForeignLegacyWal;

/// <summary>
/// Defect fixed by PR #3027 (guards: LegacyWalPageBound_Tests, ConvertedWalBesideLegacyHeader_Tests
/// and the 5.0.21 compatibility script). Legacy (5.x) WAL pages carry no checksum and nothing that ties
/// them to their data file, and the open replayed every committed page of the log beside a 5.0.21 data
/// file into it: the log of another database overwrote the header and wrote that database's pages into
/// this file, then the log was deleted, so the file's own collections were gone. Fixed: a committed
/// legacy header whose creation time is not the data file's (and a committed page that is not a page or
/// lies beyond both files, or checksummed frames beside a legacy header) fails the open with
/// INVALID_DATABASE and changes neither file.
///
/// crash.db of LiteDB.Tests/Resources/WalCrash_5_0_21.zip was written by the LiteDB 5.0.21 package (100
/// documents {_id, value: 0} checkpointed; its own WAL adds 20 updates to value 7 and _id 100).
/// foreign-log.db of ForeignWal_5_0_21.zip is the WAL of another database written by 5.0.21: pages 1730
/// to 1732 of a new collection "fresh" and its committed header (LastPageID 1732), left by a killed
/// process. The repro places it beside crash.db as crash-log.db.
///
/// Bounded damage: of the variants the guards cover, this one is chosen because the known-bad writes at
/// most pages 0 to 1732 (14 MB). The fixture digests pin that, and RLIMIT_FSIZE caps every file this
/// process writes at 64 MiB, so a torn or foreign page naming a far page ID fails with EFBIG instead of
/// growing a sparse file to terabytes (as the converted-WAL variant does on the known-bad).
///
/// Exit code 0: the defect reproduced (the known-bad LiteDB must do this). Exit code 1: the fixed
/// behavior was verified in full (the candidate must do this). Exit code 2: anything else.
/// </summary>
internal static class Program
{
    private const int Reproduced = 0;
    private const int Fixed = 1;
    private const int Inconclusive = 2;
    private const int PageSize = 8192;
    private const ulong FileSizeLimit = 64UL << 20;
    private const string CrashDigest = "a02cbef380ddb854b291614080ae974bf3de6832677c702e776fe98de32bbce7";
    private const string ForeignDigest = "0ef3c0ef9b2ebed5c1193bf6054843d39c792eb668d09c9563610ccd5d8a519a";

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var directory = Path.Combine(context.SharedDatabaseRoot ?? Path.GetTempPath(), "Issue_3027_ForeignLegacyWal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var engine = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {engine.Location}");
        // A development prerelease build (LITEDB_PREDEV) refuses to open files until this is acknowledged.
        engine.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

        try
        {
            FileSizeGuard.Apply(host, FileSizeLimit);
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
        var original = Fixture("WalCrash_5_0_21.zip", "crash.db");
        var foreign = Fixture("ForeignWal_5_0_21.zip", "foreign-log.db");
        Require(Digest(original) == CrashDigest && Digest(foreign) == ForeignDigest, "the embedded fixtures are not the committed 5.0.21 files");
        Require(original[59] == 8, "crash.db is not a 5.0.21 (file version 8) file");
        var highest = Enumerable.Range(0, foreign.Length / PageSize).Max(i => BitConverter.ToUInt32(foreign, i * PageSize));
        var bound = (highest + 1L) * PageSize;
        Require(bound < (long)FileSizeLimit, $"the foreign log names page {highest}, beyond the file size guard");
        File.WriteAllBytes(data, original);
        File.WriteAllBytes(log, foreign);

        LiteException? refusal = null;
        string seen = "";
        try
        {
            using var db = new LiteDatabase($"Filename={data}");
            seen = Describe(db);
        }
        catch (LiteException error)
        {
            refusal = error;
        }
        var dataAfter = File.ReadAllBytes(data);
        var logAfter = File.Exists(log) ? File.ReadAllBytes(log) : null;
        host.SendLog($"Open of crash.db beside the log of another database: {(refusal != null ? $"LiteException {refusal.ErrorCode}: {refusal.Message}" : "opened, " + seen)}; " +
            $"data file {original.Length} to {dataAfter.Length} bytes, log {foreign.Length} to {(logAfter == null ? "deleted" : logAfter.Length + " bytes")}");
        Require(dataAfter.Length <= bound, $"the data file grew to {dataAfter.Length} bytes, beyond the foreign log's pages ({bound} bytes)");

        if (refusal == null)
        {
            Require(!dataAfter.AsSpan().SequenceEqual(original) || logAfter == null || !logAfter.AsSpan().SequenceEqual(foreign),
                $"the open succeeded but changed neither file ({seen})");
            return (Reproduced, "REPRODUCED: the open replayed the 5.0.21 log of another database into this data file " +
                $"({original.Length} to {dataAfter.Length} bytes, log {(logAfter == null ? "deleted" : logAfter.Length + " bytes")}): {seen}");
        }

        Require(refusal.ErrorCode == LiteException.INVALID_DATABASE && refusal.Message.Contains("commits the header of another database"),
            $"the open failed for another reason: LiteException {refusal.ErrorCode}: {refusal.Message}");
        Require(dataAfter.AsSpan().SequenceEqual(original) && logAfter != null && logAfter.AsSpan().SequenceEqual(foreign),
            "the refused open changed the data file or the log");

        // Control: beside its own 5.0.21 WAL the same data file opens with every commit.
        File.WriteAllBytes(log, Fixture("WalCrash_5_0_21.zip", "crash-log.db"));
        using (var db = new LiteDatabase($"Filename={data}"))
        {
            var own = Describe(db);
            Require(own == "collections [docs], 101 documents, 21 with value 7", $"beside its own WAL: {own}, expected 101 documents, 21 with value 7");
        }
        return (Fixed, "FIXED: the open beside the 5.0.21 log of another database failed with INVALID_DATABASE and changed neither file; " +
            "beside its own WAL the data file opened with every commit (101 documents, 21 with value 7)");
    }

    private static string Describe(LiteDatabase db)
    {
        var names = db.GetCollectionNames().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var docs = names.Contains("docs") ? db.GetCollection("docs").FindAll().ToList() : new List<BsonDocument>();
        return $"collections [{string.Join(",", names)}], {docs.Count} documents, {docs.Count(x => x["value"] == 7)} with value 7";
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Fixture(string archive, string entry)
    {
        using var resource = typeof(Program).Assembly.GetManifestResourceStream(archive)
            ?? throw new InvalidOperationException($"{archive} is not embedded");
        using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
        using var source = (zip.GetEntry(entry) ?? throw new InvalidOperationException($"{archive} has no {entry}")).Open();
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Caps the size of every file this process writes (RLIMIT_FSIZE) and ignores SIGXFSZ, so a write
    /// past the cap fails with EFBIG (an IOException, exit 2) instead of growing a sparse file.
    /// </summary>
    private static class FileSizeGuard
    {
        private const int RLimitFileSize = 1;
        private const int Sigxfsz = 25;

        [StructLayout(LayoutKind.Sequential)]
        private struct RLimit
        {
            public ulong Current;
            public ulong Maximum;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int getrlimit(int resource, out RLimit limit);

        [DllImport("libc", SetLastError = true)]
        private static extern int setrlimit(int resource, ref RLimit limit);

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr signal(int signal, IntPtr handler);

        public static void Apply(ReproHostClient host, ulong bytes)
        {
            Require(OperatingSystem.IsLinux(), "the file size guard (RLIMIT_FSIZE) needs Linux");
            Require(signal(Sigxfsz, (IntPtr)1) != (IntPtr)(-1), $"signal(SIGXFSZ, SIG_IGN) failed (errno {Marshal.GetLastWin32Error()})");
            Require(getrlimit(RLimitFileSize, out var limit) == 0, $"getrlimit failed (errno {Marshal.GetLastWin32Error()})");
            limit.Current = Math.Min(limit.Maximum, bytes);
            Require(setrlimit(RLimitFileSize, ref limit) == 0, $"setrlimit failed (errno {Marshal.GetLastWin32Error()})");
            host.SendLog($"File size guard: RLIMIT_FSIZE {limit.Current} bytes");
        }
    }
}
