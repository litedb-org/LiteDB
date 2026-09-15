using LiteDB;

internal static class Issue2854Scenario
{
    public const string DropOperation = "DropCollection";
    public const string PageListOperation = "$page_list/data-lasso";
    public const string EmptyPageListOperation = "$page_list/empty-list";
    public const string CorruptionMarker = "FILE_CORRUPTED_2854";
    public const string OperationStartedMarker = "OPERATION_2854_STARTED";
    public const string HealthyDropMarker = "HEALTHY_DROP_2854_OK";
    public const string HealthyPageListMarker = "HEALTHY_PAGE_LIST_2854_OK";
    public const int ExpectedCorruptionExitCode = 10;
    public const int UnsafeCompletionExitCode = 30;

    private const string CollectionName = "cycle_target";
    private const string EmptySeedCollectionName = "empty_page_seed";
    private const int DocumentCount = 128;
    private const int EmptySeedDocumentCount = 32;
    private const int UnexpectedFailureExitCode = 40;

    public static void CreatePristineDatabase(string path)
    {
        using (var db = Open(path))
        {
            db.CheckpointSize = 0;
            var collection = db.GetCollection(CollectionName);
            var inserted = collection.InsertBulk(Enumerable.Range(1, DocumentCount).Select(id =>
                new BsonDocument
                {
                    ["_id"] = id,
                    ["payload"] = ExpectedPayload(id)
                }));
            if (inserted != DocumentCount)
            {
                throw new InvalidOperationException($"inserted {inserted} of {DocumentCount} fixture rows");
            }

            var seed = db.GetCollection(EmptySeedCollectionName);
            var seeded = seed.InsertBulk(Enumerable.Range(1, EmptySeedDocumentCount).Select(id =>
                new BsonDocument
                {
                    ["_id"] = id,
                    ["payload"] = ExpectedPayload(id + DocumentCount)
                }));
            if (seeded != EmptySeedDocumentCount)
            {
                throw new InvalidOperationException(
                    $"inserted {seeded} of {EmptySeedDocumentCount} empty-page seed rows");
            }

            db.Checkpoint();
            if (!db.DropCollection(EmptySeedCollectionName))
            {
                throw new InvalidOperationException("could not drop the empty-page seed collection");
            }
            db.Checkpoint();
        }

        using var reopened = Open(path);
        VerifyRows(reopened);
    }

    public static int RunChild(string mode, string path)
    {
        try
        {
            return mode switch
            {
                "drop-healthy" => DropHealthy(path),
                "page-list-healthy" => WalkHealthyPageList(path),
                "drop-corrupt" => DropCorrupt(path),
                "page-list-corrupt" => WalkCorruptPageList(path, PageListOperation),
                "empty-page-list-corrupt" => WalkCorruptPageList(path, EmptyPageListOperation),
                _ => throw new InvalidOperationException("unknown child mode: " + mode)
            };
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            return UnexpectedFailureExitCode;
        }
    }

    private static int DropHealthy(string path)
    {
        using (var db = Open(path))
        {
            VerifyRows(db);
            if (!db.DropCollection(CollectionName) || db.CollectionExists(CollectionName))
            {
                throw new InvalidOperationException("healthy drop was a no-op");
            }
        }

        using var reopened = Open(path);
        if (reopened.CollectionExists(CollectionName) || reopened.GetCollectionNames().Any())
        {
            throw new InvalidOperationException("healthy drop did not persist");
        }

        Console.WriteLine(HealthyDropMarker);
        return 0;
    }

    private static int WalkHealthyPageList(string path)
    {
        var layout = RawPageListFixture.InspectHealthy(path);
        var emptyLayout = RawPageListFixture.InspectHealthyEmptyList(path);
        var expectedData = layout.DataPageIds.ToHashSet();
        var expectedEmpty = emptyLayout.PageIds.ToHashSet();
        var observedData = new HashSet<uint>();
        var observedEmpty = new HashSet<uint>();

        using var db = Open(path);
        using var reader = db.Execute("SELECT $ FROM $page_list");
        while (reader.Read())
        {
            var row = reader.Current.AsDocument;
            if (!row["pageType"].IsString)
            {
                continue;
            }

            var value = row["pageID"];
            if (!value.IsInt32 || value.AsInt32 < 0)
            {
                throw new InvalidOperationException("healthy $page_list returned an invalid page ID");
            }

            if (row["pageType"].AsString == "Empty")
            {
                if (!observedEmpty.Add((uint)value.AsInt32))
                {
                    throw new InvalidOperationException("healthy $page_list returned a duplicate empty page");
                }
            }
            else if (row["pageType"].AsString == "Data" &&
                row["collection"].IsString && row["collection"].AsString == CollectionName &&
                !observedData.Add((uint)value.AsInt32))
            {
                throw new InvalidOperationException("healthy $page_list returned a duplicate target data page");
            }
        }

        if (!expectedData.SetEquals(observedData) || !expectedEmpty.SetEquals(observedEmpty))
        {
            throw new InvalidOperationException(
                $"healthy $page_list returned data={observedData.Count}/{expectedData.Count}, " +
                $"empty={observedEmpty.Count}/{expectedEmpty.Count}");
        }

        Console.WriteLine(
            $"{HealthyPageListMarker} dataPages={observedData.Count}, emptyPages={observedEmpty.Count}");
        return 0;
    }

    private static int DropCorrupt(string path)
    {
        try
        {
            using (var db = Open(path))
            {
                VerifyRows(db);
                AnnounceOperationStart(DropOperation);
                if (!db.DropCollection(CollectionName))
                {
                    Console.WriteLine($"UNSAFE_COMPLETION_2854: {DropOperation}: returned false");
                    return UnsafeCompletionExitCode;
                }
            }

            using var reopened = Open(path);
            var absent = !reopened.CollectionExists(CollectionName) && !reopened.GetCollectionNames().Any();
            Console.WriteLine(absent
                ? $"UNSAFE_COMPLETION_2854: {DropOperation}: silently completed"
                : $"UNSAFE_COMPLETION_2854: {DropOperation}: left ambiguous collection state");
            return UnsafeCompletionExitCode;
        }
        catch (LiteException exception) when (exception.ErrorCode == LiteException.INVALID_DATAFILE_STATE)
        {
            Console.WriteLine($"{CorruptionMarker}: {DropOperation}: {exception.Message}");
            return ExpectedCorruptionExitCode;
        }
        catch (InvalidCastException exception)
        {
            Console.WriteLine($"UNSAFE_EXCEPTION_2854: {DropOperation}: {exception.Message}");
            return UnsafeCompletionExitCode;
        }
    }

    private static int WalkCorruptPageList(string path, string operation)
    {
        try
        {
            using var db = Open(path);
            AnnounceOperationStart(operation);
            using var reader = db.Execute("SELECT $ FROM $page_list");
            var rows = 0;
            while (reader.Read())
            {
                rows++;
            }
            Console.WriteLine($"UNSAFE_COMPLETION_2854: {operation}: returned {rows} rows");
            return UnsafeCompletionExitCode;
        }
        catch (LiteException exception) when (exception.ErrorCode == LiteException.INVALID_DATAFILE_STATE)
        {
            Console.WriteLine($"{CorruptionMarker}: {operation}: {exception.Message}");
            return ExpectedCorruptionExitCode;
        }
    }

    public static string GetOperationStartMarker(string operation) =>
        $"{OperationStartedMarker}: {operation}";

    private static void AnnounceOperationStart(string operation)
    {
        Console.WriteLine(GetOperationStartMarker(operation));
        Console.Out.Flush();
    }

    private static LiteDatabase Open(string path) => new(new ConnectionString
    {
        Filename = path,
        Connection = ConnectionType.Direct,
        AutoRebuild = false
    });

    private static void VerifyRows(LiteDatabase db)
    {
        var rows = db.GetCollection(CollectionName).FindAll()
            .OrderBy(row => row["_id"].AsInt32)
            .ToArray();
        if (rows.Length != DocumentCount)
        {
            throw new InvalidOperationException($"fixture contains {rows.Length} of {DocumentCount} rows");
        }
        for (var index = 0; index < rows.Length; index++)
        {
            var id = index + 1;
            if (rows[index]["_id"].AsInt32 != id || rows[index]["payload"].AsString != ExpectedPayload(id))
            {
                throw new InvalidOperationException("fixture row verification failed at ID " + id);
            }
        }
    }

    private static string ExpectedPayload(int id) =>
        new string((char)('a' + id % 26), 1_700) + ":" + id;
}
