using LiteDB;
using System.Security.Cryptography;

internal static class UpgradeScenario
{
    public const string HealthyMode = "healthy";
    public const string SelfLoopMode = "self-loop";
    public const string PastEofMode = "past-eof";
    public const string StartedMarker = "UPGRADE_STARTED_2826";
    public const string HealthyMarker = "HEALTHY_UPGRADE_2826_OK";
    public const string FixedExceptionMarker = "FIXED_LITE_EXCEPTION_2826";
    public const string FixedErrorsMarker = "FIXED_ERROR_LEDGER_2826";
    public const string KnownBugMarker = "KNOWN_BUG_2826";

    public const int FixedExitCode = 10;
    public const int HarnessFailureExitCode = 20;
    public const int KnownBugExitCode = 30;

    public static int RunChild(string mode, string path)
    {
        if (mode is not (HealthyMode or SelfLoopMode or PastEofMode))
        {
            Console.Error.WriteLine("unknown child mode: " + mode);
            return HarnessFailureExitCode;
        }

        Console.WriteLine($"{StartedMarker}: mode={mode}");
        Console.Out.Flush();
        var preOpenFiles = SnapshotDirectory(path);

        LiteDatabase database;
        try
        {
            database = Open(path, upgrade: true);
        }
        catch (LiteException exception) when (mode != HealthyMode)
        {
            Require(SnapshotDirectory(path).SequenceEqual(preOpenFiles),
                "graceful rejection changed the data file or its directory sidecars");
            Console.WriteLine($"{FixedExceptionMarker}: mode={mode}, code={exception.ErrorCode}, message={exception.Message}");
            return FixedExitCode;
        }
        catch (OutOfMemoryException) when (mode != HealthyMode)
        {
            Console.WriteLine($"{KnownBugMarker}: mode={mode}, kind=OutOfMemoryException");
            return KnownBugExitCode;
        }
        catch (NullReferenceException exception) when (
            mode == PastEofMode && IsV7ReaderFailure(exception))
        {
            Console.WriteLine($"{KnownBugMarker}: mode={mode}, kind=NullReferenceException");
            return KnownBugExitCode;
        }
        catch (IOException exception) when (
            mode == SelfLoopMode &&
            exception.Message.Contains("Stream was too long", StringComparison.OrdinalIgnoreCase) &&
            IsV7ReaderFailure(exception))
        {
            Console.WriteLine($"{KnownBugMarker}: mode={mode}, kind=StreamWasTooLong");
            return KnownBugExitCode;
        }
        catch (Exception exception)
        {
            return Fail(exception);
        }

        try
        {
            using (database)
            {
                if (mode == HealthyMode)
                {
                    VerifyDocuments(database, requireLarge: true);
                    Require(!database.CollectionExists("_rebuild_errors"),
                        "healthy upgrade unexpectedly created _rebuild_errors");
                }
                else
                {
                    VerifyCompletedCorruptUpgrade(database);
                }
            }

            using (var reopened = Open(path, upgrade: false))
            {
                if (mode == HealthyMode)
                {
                    VerifyDocuments(reopened, requireLarge: true);
                }
                else
                {
                    VerifyCompletedCorruptUpgrade(reopened);
                }
            }

            if (mode == HealthyMode)
            {
                Console.WriteLine(HealthyMarker);
                return 0;
            }

            Console.WriteLine($"{FixedErrorsMarker}: mode={mode}, intact document and error ledger persisted");
            return FixedExitCode;
        }
        catch (Exception exception)
        {
            return Fail(exception);
        }
    }

    private static void VerifyCompletedCorruptUpgrade(LiteDatabase database)
    {
        VerifyDocuments(database, requireLarge: false);
        Require(database.CollectionExists("_rebuild_errors"),
            "completed corrupt upgrade did not create _rebuild_errors");
        var errors = database.GetCollection("_rebuild_errors").FindAll().ToArray();
        Require(errors.Length > 0 && errors.All(x => x.Count > 0),
            "completed corrupt upgrade did not persist a nonempty error row");
        var errorJson = errors.Select(x => x.ToString()).ToArray();
        Require(errorJson.Any(x =>
                x.Contains("5", StringComparison.Ordinal) ||
                x.Contains("11", StringComparison.Ordinal) ||
                x.Contains("extend", StringComparison.OrdinalIgnoreCase) ||
                x.Contains("page", StringComparison.OrdinalIgnoreCase)),
            "error ledger does not identify the damaged page chain");
    }

    private static void VerifyDocuments(LiteDatabase database, bool requireLarge)
    {
        Require(database.CollectionExists("documents"), "documents collection is missing after upgrade");
        var documents = database.GetCollection("documents").FindAll().ToArray();
        Require(documents.Length is 1 or 2, $"upgrade produced {documents.Length} documents instead of one or two");
        Require(documents.All(x => x["_id"].IsString), "upgrade produced a non-string fixture ID");
        Require(documents.Select(x => x["_id"].AsString).Distinct(StringComparer.Ordinal).Count() == documents.Length,
            "upgrade produced duplicate fixture IDs");

        var healthy = documents.SingleOrDefault(x => x["_id"].AsString == V7FixtureInspector.HealthyId);
        Require(healthy != null, "intact control document was lost");
        VerifyExactDocument(healthy!, "control", V7FixtureInspector.HealthyPayload);

        var damaged = documents.SingleOrDefault(x => x["_id"].AsString == V7FixtureInspector.DamagedId);
        Require(!requireLarge || damaged != null, "healthy upgrade lost the large document");
        Require(documents.All(x => x["_id"].AsString is V7FixtureInspector.HealthyId or V7FixtureInspector.DamagedId),
            "upgrade invented an unexpected document");
        if (damaged != null)
        {
            VerifyExactDocument(damaged, "extended", null);
        }
    }

    private static void VerifyExactDocument(BsonDocument document, string kind, string? payload)
    {
        Require(document.Count == 3 && document.ContainsKey("_id") &&
            document.ContainsKey("kind") && document.ContainsKey("payload"),
            "upgraded document fields changed");
        Require(document["kind"].IsString && document["kind"].AsString == kind && document["payload"].IsString,
            "upgraded document field values changed");
        var actual = document["payload"].AsString;
        if (payload != null)
        {
            Require(actual == payload, "intact control payload changed");
        }
        else
        {
            Require(actual.Length == V7FixtureInspector.LargePayloadLength && actual.All(x => x == 'x'),
                "large document payload is not exactly 20,000 x characters");
        }
    }

    private static LiteDatabase Open(string path, bool upgrade) => new(new ConnectionString
    {
        Filename = path,
        Connection = ConnectionType.Direct,
        Upgrade = upgrade
    });

    private static bool IsV7ReaderFailure(Exception exception)
    {
        var stack = exception.StackTrace;
        return stack != null && stack.Contains("LiteDB.Engine.FileReaderV7", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> SnapshotDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath)!;
        return Directory.EnumerateFiles(directory)
            .OrderBy(x => Path.GetFileName(x), StringComparer.Ordinal)
            .Select(x =>
            {
                var bytes = File.ReadAllBytes(x);
                return $"{Path.GetFileName(x)}:{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes))}";
            })
            .ToArray();
    }

    private static int Fail(Exception exception)
    {
        Console.Error.WriteLine(exception);
        return HarnessFailureExitCode;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
