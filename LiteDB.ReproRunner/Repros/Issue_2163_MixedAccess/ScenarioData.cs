using System.Security.Cryptography;
using System.Text;
using LiteDB;

namespace Issue_2163_MixedAccess;

internal static class ScenarioData
{
    public const int BaselineId = 101;
    public const int SharedDuringOwnerId = 202;
    public const int DirectAfterSharedId = 303;
    public const int SharedAfterReleaseId = 404;
    private const string CollectionName = "ledger";

    public static WriteReceipt CreateReceipt(int id, string writer, int processId)
    {
        var token = $"issue2163/{writer}/{id}";
        var payload = $"writer={writer};id={id};" + new string((char)('A' + id % 26), 2048);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}|{token}|{payload}")));
        return new WriteReceipt(id, writer, token, payload, digest, processId);
    }

    public static LiteDatabase Open(string path, ConnectionType connection) => new(new ConnectionString
    {
        Filename = path,
        Connection = connection
    });

    public static ILiteCollection<BsonDocument> Collection(LiteDatabase database) =>
        database.GetCollection<BsonDocument>(CollectionName);

    public static void Insert(ILiteCollection<BsonDocument> collection, WriteReceipt receipt) =>
        collection.Insert(receipt.ToDocument());

    public static void VerifyExact(LiteDatabase database, WriteReceipt receipt)
    {
        var collection = Collection(database);
        var byId = collection.FindById(receipt.Id);

        if (byId is null || !receipt.Matches(byId))
        {
            throw new InvalidDataException($"Row {receipt.Id} was not readable by id with its exact contents.");
        }

        var indexed = collection.Find(Query.EQ("token", receipt.Token)).ToArray();
        if (indexed.Length != 1 || !receipt.Matches(indexed[0]))
        {
            throw new InvalidDataException($"The token index did not resolve exact row {receipt.Id}.");
        }
    }

    public static void WriteReceipt(string directory, WriteReceipt receipt)
    {
        Directory.CreateDirectory(directory);
        Coordination.WriteJson(Path.Combine(directory, $"{receipt.Id:D4}.json"), receipt);
    }

    public static IReadOnlyList<WriteReceipt> ReadReceipts(string directory)
    {
        if (!Directory.Exists(directory)) return Array.Empty<WriteReceipt>();

        var receipts = Directory.GetFiles(directory, "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(Coordination.ReadJson<WriteReceipt>)
            .ToArray();

        if (receipts.Select(receipt => receipt.Id).Distinct().Count() != receipts.Length)
        {
            throw new InvalidDataException("The external ledger contains duplicate receipt ids.");
        }

        return receipts;
    }
}

internal sealed record WriteReceipt(
    int Id,
    string Writer,
    string Token,
    string Payload,
    string Digest,
    int ProcessId)
{
    public BsonDocument ToDocument() => new()
    {
        ["_id"] = Id,
        ["writer"] = Writer,
        ["token"] = Token,
        ["payload"] = Payload,
        ["digest"] = Digest,
        ["processId"] = ProcessId
    };

    public bool Matches(BsonDocument document)
    {
        return document.TryGetValue("_id", out var id) && id.IsInt32 && id.AsInt32 == Id &&
            document.TryGetValue("writer", out var writer) && writer.IsString && writer.AsString == Writer &&
            document.TryGetValue("token", out var token) && token.IsString && token.AsString == Token &&
            document.TryGetValue("payload", out var payload) && payload.IsString && payload.AsString == Payload &&
            document.TryGetValue("digest", out var digest) && digest.IsString && digest.AsString == Digest &&
            document.TryGetValue("processId", out var pid) && pid.IsInt32 && pid.AsInt32 == ProcessId;
    }
}

internal static class DatabaseOracle
{
    public static (DatabaseSnapshot First, DatabaseSnapshot Second) ReopenTwice(
        string path,
        IReadOnlyList<WriteReceipt> receipts)
    {
        var first = Inspect(path, receipts, checkpoint: true);
        var second = Inspect(path, receipts, checkpoint: false);
        return (first, second);
    }

    private static DatabaseSnapshot Inspect(
        string path,
        IReadOnlyList<WriteReceipt> receipts,
        bool checkpoint)
    {
        using var database = ScenarioData.Open(path, ConnectionType.Direct);
        var collection = ScenarioData.Collection(database);
        var documents = collection.FindAll().ToArray();
        var expected = receipts.ToDictionary(receipt => receipt.Id);
        var actual = new Dictionary<int, BsonDocument>();
        var malformed = new List<string>();

        foreach (var document in documents)
        {
            if (!document.TryGetValue("_id", out var value) || !value.IsInt32)
            {
                malformed.Add("document with absent or non-Int32 _id");
                continue;
            }

            if (!actual.TryAdd(value.AsInt32, document))
            {
                malformed.Add($"duplicate _id {value.AsInt32}");
            }
        }

        var missing = expected.Keys.Except(actual.Keys).OrderBy(id => id).ToArray();
        var unexpected = actual.Keys.Except(expected.Keys).OrderBy(id => id).ToArray();
        var mismatched = expected.Keys.Intersect(actual.Keys)
            .Where(id => !expected[id].Matches(actual[id]))
            .OrderBy(id => id)
            .ToArray();
        var indexMismatched = new List<int>();

        foreach (var receipt in receipts)
        {
            var indexed = collection.Find(Query.EQ("token", receipt.Token)).ToArray();
            var expectedCount = missing.Contains(receipt.Id) ? 0 : 1;
            if (indexed.Length != expectedCount ||
                (expectedCount == 1 && !receipt.Matches(indexed[0])))
            {
                indexMismatched.Add(receipt.Id);
            }
        }

        if (checkpoint) database.Checkpoint();

        return new DatabaseSnapshot(
            documents.Length,
            missing,
            unexpected,
            mismatched,
            indexMismatched.ToArray(),
            malformed.ToArray());
    }
}

internal sealed record DatabaseSnapshot(
    int DocumentCount,
    int[] MissingIds,
    int[] UnexpectedIds,
    int[] MismatchedIds,
    int[] IndexMismatchedIds,
    string[] MalformedDocuments)
{
    public bool IsClean =>
        MissingIds.Length == 0 &&
        UnexpectedIds.Length == 0 &&
        MismatchedIds.Length == 0 &&
        IndexMismatchedIds.Length == 0 &&
        MalformedDocuments.Length == 0;

    public bool IsOnlyMissing(int id) =>
        MissingIds.SequenceEqual(new[] { id }) &&
        UnexpectedIds.Length == 0 &&
        MismatchedIds.Length == 0 &&
        IndexMismatchedIds.All(indexedId => indexedId == id) &&
        MalformedDocuments.Length == 0;

    public string Describe() =>
        $"count={DocumentCount}, missing=[{string.Join(',', MissingIds)}], " +
        $"unexpected=[{string.Join(',', UnexpectedIds)}], mismatched=[{string.Join(',', MismatchedIds)}], " +
        $"indexMismatched=[{string.Join(',', IndexMismatchedIds)}], " +
        $"malformed=[{string.Join("; ", MalformedDocuments)}]";
}
