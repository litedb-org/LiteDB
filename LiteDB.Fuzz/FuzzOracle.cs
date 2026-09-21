using System.Runtime.CompilerServices;

namespace LiteDB.Fuzz;

internal static class FuzzOracle
{
    internal static bool DocumentsEqual(IReadOnlyList<BsonDocument> actual,
        IReadOnlyList<BsonDocument> expected) => actual.Count == expected.Count &&
        actual.Zip(expected, (left, right) => BsonSerializer.Serialize(left)
            .SequenceEqual(BsonSerializer.Serialize(right))).All(equal => equal);

    internal static void VerifyExactDocuments(FuzzContext context, IReadOnlyList<BsonDocument> actual,
        IReadOnlyList<BsonDocument> expected, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        context.Check(DocumentsEqual(actual, expected), message, file, line);

    internal static void VerifyAtomicDocuments(FuzzContext context, IReadOnlyList<BsonDocument> actual,
        IReadOnlyList<BsonDocument> acknowledged, IReadOnlyList<BsonDocument> inFlight, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        context.Check(DocumentsEqual(actual, acknowledged) || DocumentsEqual(actual, inFlight), message, file, line);

    internal static void VerifyCrashMarker(FuzzContext context, string actual, string expected,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        context.Check(actual == expected, $"Shared crash marker was invalid: {actual}.", file, line);

    internal static void VerifyBsonValue(FuzzContext context, BsonValue actual, BsonValue expected,
        string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        context.Check(actual.Equals(expected), message, file, line);

    internal static void VerifyDistinct<T>(FuzzContext context, IEnumerable<T> values,
        IEqualityComparer<T> comparer, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var materialized = values.ToArray();
        context.Check(materialized.Distinct(comparer).Count() == materialized.Length, message, file, line);
    }

    internal static void VerifySequence<T>(FuzzContext context, IEnumerable<T> actual,
        IEnumerable<T> expected, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        context.Check(actual.SequenceEqual(expected), message, file, line);

    internal static void VerifySnapshotResult(FuzzContext context, string result) =>
        _ = result == "ok" ? true : throw new FuzzFailureException("SNAPSHOT_IMPOSSIBLE_VIEW",
            "A long-lived reader observed data outside its starting snapshot.");

    internal static void VerifyVectorScore(FuzzContext context, double actual, double expected,
        double tolerance, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        context.Check(Math.Abs(actual - expected) <= tolerance, message, file, line);
}
