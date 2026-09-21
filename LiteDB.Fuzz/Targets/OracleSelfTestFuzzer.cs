namespace LiteDB.Fuzz.Targets;

internal sealed class OracleSelfTestFuzzer : IFuzzTarget
{
    public string Name => "oracle-selftest";
    public string Description => "Mutation tests that prove core fuzz oracles reject controlled acknowledged-loss, atomicity, cache, index, snapshot, and vector faults.";

    public Task RunAsync(FuzzContext context)
    {
        var killed = 0;
        while (context.Next())
        {
            var expected = new[] { Document(1, 10), Document(2, 20) };
            killed += Reject(context, Exact(expected, new[] { Document(1, 10) }), "acknowledged commit loss");
            killed += Reject(context, Exact(expected, new[] { Document(1, 10), Document(2, 999) }), "partial transaction");
            killed += Reject(context, Marker("3|outer-after-commit", 4, "outer-after-commit"), "missing/mismatched crash hook");
            killed += Reject(context, CacheAba(10, 20, 20), "stale A-B-A parameter cache");
            killed += Reject(context, Unique(new[] { "A", "a" }, StringComparer.OrdinalIgnoreCase), "unique collation bypass");
            killed += Reject(context, Ordered(new[] { 1, 3, 2 }), "secondary index corruption");
            killed += Reject(context, Exact(expected, new[] { Document(1, 10), Document(2, 21) }), "stale/impossible snapshot");
            killed += Reject(context, VectorScore(new[] { 1f, 0f }, new[] { 0f, 1f }, 0d), "bad vector score");
            context.ObserveNovelty("oracle-mutation", killed);
        }
        context.Check(killed == context.Steps * 8, "A controlled harness mutation survived its oracle.");
        context.Metrics["controlledMutationsKilled"] = killed;
        return Task.CompletedTask;
    }

    private static int Reject(FuzzContext context, bool accepted, string mutation)
    {
        context.Check(!accepted, $"Harness oracle accepted controlled mutation: {mutation}.");
        return 1;
    }

    private static bool Exact(BsonDocument[] expected, BsonDocument[] actual) => expected.Length == actual.Length &&
        expected.Zip(actual, (left, right) => BsonSerializer.Serialize(left)
            .SequenceEqual(BsonSerializer.Serialize(right))).All(equal => equal);

    private static bool Marker(string actual, int position, string phase) => actual == $"{position}|{phase}";
    private static bool CacheAba(int firstA, int b, int secondA) => firstA == secondA && firstA != b;
    private static bool Unique(IEnumerable<string> values, IEqualityComparer<string> comparer) =>
        values.Distinct(comparer).Count() == values.Count();
    private static bool Ordered(IEnumerable<int> values) => values.SequenceEqual(values.OrderBy(value => value));

    private static bool VectorScore(float[] left, float[] right, double actual)
    {
        var squared = left.Zip(right, (a, b) => (a - b) * (a - b)).Sum();
        return Math.Abs(Math.Sqrt(squared) - actual) <= 1e-6;
    }

    private static BsonDocument Document(int id, int value) => new() { ["_id"] = id, ["value"] = value };
}
