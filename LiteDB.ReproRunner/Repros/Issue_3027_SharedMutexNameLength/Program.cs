using System.Reflection;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3027_SharedMutexNameLength;

/// <summary>
/// Regression since 5.0.21, found in PR #3027 and fixed by the #3051 S02 extraction
/// (branch split/02-unix-mutex-names; guard: SharedMutexNameLength_Tests). Shared mode names
/// its named mutexes after the URI-escaped full path of the database (#2709); the SHA-1 fallback that
/// keeps the name short ran only on Windows. On Linux and macOS a path whose escaped form exceeds the
/// runtime's named-mutex limit made every shared open throw ArgumentException from the Mutex
/// constructor. Non-ASCII directory names reach the limit quickly: each Cyrillic letter escapes to six
/// characters. 5.0.21 always used the SHA-1 of the lower-cased full path (40 characters), so the same
/// path opened and wrote in shared mode. Fixed: Unix names are hashed beyond the runtime limit.
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
        var root = Path.Combine(Path.GetTempPath(), "rr3027-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var engine = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {engine.Location}");
        // A development prerelease build (LITEDB_PREDEV) refuses to open files until this is acknowledged.
        engine.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

        try
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows always hashed long mutex names; the defect is Unix-only.");
            // 48 Cyrillic letters: 288 characters once escaped, beyond the runtime's 255.
            var directory = Directory.CreateDirectory(Path.Combine(root, string.Concat(Enumerable.Repeat("данные", 8)))).FullName;
            var (code, summary) = Run(host, Path.Combine(directory, "app.db"));
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
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static (int Code, string Summary) Run(ReproHostClient host, string path)
    {
        var connection = $"Filename={path};Connection=shared";
        host.SendLog($"Database path: {path.Length} characters, {Uri.EscapeDataString(path).Length} once escaped");
        try
        {
            using (var db = new LiteDatabase(connection))
            {
                db.GetCollection("items").Insert(new BsonDocument { ["_id"] = 1 });
            }
        }
        catch (ArgumentException error) when (error.StackTrace?.Contains("System.Threading.Mutex", StringComparison.Ordinal) == true)
        {
            host.SendLog($"Shared open threw {error.GetType().Name} from the Mutex constructor: {error.Message}");
            return (Reproduced, $"REPRODUCED: a shared connection cannot open a database in a directory with a non-ASCII name: " +
                $"{error.GetType().Name} from the Mutex constructor: {error.Message}");
        }

        using (var db = new LiteDatabase(connection))
        {
            var count = db.GetCollection("items").Count();
            Require(count == 1, $"the reopened shared connection found {count} documents, expected 1");
        }
        return (Fixed, "FIXED: a shared connection opened, wrote and reopened a database in a directory with a non-ASCII name");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
