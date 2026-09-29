using System.IO.Compression;
using System.Reflection;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3027_LegacyReadOnlyStream;

/// <summary>
/// Regression since 5.0.21 fixed by the caller-stream slice of PR #3027 (guard: LegacyReadOnlyStream_Tests). <c>new
/// LiteDatabase(stream)</c> over a stream that cannot be written (a FileStream opened with
/// FileAccess.Read, an embedded resource, a read-only share) opened a writable engine, and every
/// writable open of a 5.0.21 file migrates it first (index ordering and checksum conversion), which
/// writes to the stream: the open failed with NotSupportedException. This constructor has no ReadOnly
/// or Legacy Index Scan switch, so the file could not be read at all. 5.0.21 read the same stream
/// without writing. Fixed: an open that would have to change a non-writable stream opens read-only,
/// reads the 5.0.21 indexes by legacy scan and never writes.
///
/// customers.db of DropIndex_5_0_21.zip (LiteDB-Artifacts, pinned by LiteDB.Tests/Resources/artifacts.json) was written by the LiteDB 5.0.21
/// package: collection "customers" with indexes Name, Age and CustomerId, 200 documents
/// {_id: i, Name: "n"+i, Age: i%90, CustomerId: "C"+i}, then DropIndex("Age").
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
        var context = ReproContext.FromEnvironment();
        var directory = Path.Combine(context.SharedDatabaseRoot ?? Path.GetTempPath(), "Issue_3027_LegacyReadOnlyStream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var engine = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {engine.Location}");
        // A development prerelease build (LITEDB_PREDEV) refuses to open files until this is acknowledged.
        engine.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

        try
        {
            var (code, summary) = Run(host, Path.Combine(directory, "customers.db"));
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

    private static (int Code, string Summary) Run(ReproHostClient host, string path)
    {
        File.WriteAllBytes(path, Fixture("DropIndex_5_0_21.zip", "customers.db"));
        var original = File.ReadAllBytes(path);
        Require(original[59] == 8, "customers.db is not a 5.0.21 (file version 8) file");

        // A FileStream opened with FileAccess.Read; the WAL lives in memory.
        Outcome file;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Require(!stream.CanWrite, "the FileStream opened with FileAccess.Read is writable");
            file = Open(() => new LiteDatabase(stream));
        }
        var fileUnchanged = File.ReadAllBytes(path).AsSpan().SequenceEqual(original);
        host.SendLog($"new LiteDatabase(stream) over a read-only FileStream: {file}; customers.db {(fileUnchanged ? "unchanged" : "CHANGED")}");

        // A read-only data stream beside a writable (empty) log stream.
        using var data = new MemoryStream(original, writable: false);
        using var log = new MemoryStream();
        var logged = Open(() => new LiteDatabase(data, null, log));
        var logBytes = log.ToArray().Length;
        host.SendLog($"new LiteDatabase(stream, null, log) over a read-only data stream and an empty writable log: {logged}; " +
            $"log {logBytes} bytes");

        if (file.Failure is NotSupportedException && logged.Failure is NotSupportedException)
        {
            Require(fileUnchanged, "the failed open changed the read-only file");
            return (Reproduced, "REPRODUCED: new LiteDatabase(stream) over a read-only stream of a 5.0.21 file failed with " +
                $"{file.Failure.GetType().Name}: {file.Failure.Message} (with a writable log stream too, after writing {logBytes} bytes into it)");
        }

        Require(file.Failure == null && logged.Failure == null, $"the opens neither all failed as the defect nor all succeeded: {file}; {logged}");
        Require(file.Read == null && logged.Read == null, $"a read-only stream did not read the 5.0.21 file correctly: {file.Read ?? logged.Read}");
        Require(file.Write != null && logged.Write != null, "an insert through a read-only data stream was accepted");
        Require(fileUnchanged, "the read-only file changed");
        Require(logBytes == 0, $"the open wrote {logBytes} bytes into the log stream although it cannot migrate the data stream");

        // Control: the same file, now writable, migrates and keeps every document.
        using (var db = new LiteDatabase(path))
        {
            var control = Verify(db.GetCollection("customers"));
            Require(control == null, $"a writable open of the same file did not read it correctly: {control}");
        }
        return (Fixed, "FIXED: new LiteDatabase(stream) over a read-only stream of a 5.0.21 file opened read-only, read all 200 documents " +
            $"and their indexed queries, rejected an insert ({file.Write}) and left the file unchanged, also beside an unwritten writable log stream");
    }

    private sealed record Outcome(Exception? Failure, string? Read, string? Write)
    {
        public override string ToString() => Failure != null ? $"threw {Failure.GetType().Name}: {Failure.Message}"
            : $"opened; reads: {Read ?? "all 200 documents and their indexed queries"}; insert: {Write ?? "accepted"}";
    }

    private static Outcome Open(Func<LiteDatabase> open)
    {
        try
        {
            using var db = open();
            var read = Verify(db.GetCollection("customers"));
            var write = Rejected(() => db.GetCollection("customers").Insert(new BsonDocument { ["_id"] = 1000, ["Name"] = "n1000" }));
            return new Outcome(null, read, write);
        }
        catch (NotSupportedException error)
        {
            return new Outcome(error, null, null);
        }
    }

    /// <summary>Null when all 200 documents and the indexed queries read as 5.0.21 wrote them; else what differs.</summary>
    private static string? Verify(ILiteCollection<BsonDocument> customers)
    {
        var docs = customers.FindAll().ToList();
        var ids = docs.Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
        if (ids.Length != 200 || ids.Distinct().Count() != 200) return $"{ids.Length} documents, expected 200";
        foreach (var doc in docs)
        {
            var id = doc["_id"].AsInt32;
            if (doc["Name"] != "n" + id || doc["Age"] != id % 90 || doc["CustomerId"] != "C" + id) return $"document {doc}";
        }
        var byCustomer = customers.Find(Query.EQ("CustomerId", "C5")).Select(x => x["_id"].AsInt32).ToArray();
        if (!byCustomer.SequenceEqual(new[] { 5 })) return $"CustomerId = C5 found _id [{string.Join(",", byCustomer)}], expected 5";
        var byName = customers.Count(Query.GT("Name", "n9"));
        if (byName != 10) return $"Name > n9 counted {byName}, expected 10 (n90..n99)";
        var byAge = customers.Count(Query.EQ("Age", 7));
        if (byAge != docs.Count(x => x["Age"] == 7)) return $"Age = 7 counted {byAge}";
        return null;
    }

    private static string? Rejected(Action write)
    {
        try
        {
            write();
            return null;
        }
        catch (Exception error)
        {
            return $"{error.GetType().Name}: {error.Message}";
        }
    }

    private static byte[] Fixture(string archive, string entry)
    {
        // Pinned in LiteDB.Tests/Resources/artifacts.json; LITEDB_ARTIFACTS_DIR, cache, then download.
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
