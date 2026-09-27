using System.Collections.Concurrent;
using System.Linq.Expressions;
using LiteDB.Tests.Mapper;

namespace LiteDB.Fuzz.Targets;

internal sealed class LinqCacheFuzzer : IFuzzTarget
{
    public string Name => "linq-cache";
    public string Description => "Cached vs fresh LINQ translation, CLR evaluation, and shared-mapper concurrency.";

    public async Task RunAsync(FuzzContext context)
    {
        var mapper = new BsonMapper { EnumAsInteger = context.Seed % 2 == 0 };
        var row = new FuzzRow
        {
            Value = 7, Name = "Ready", State = FuzzState.Ready,
            Next = new FuzzRow { Value = 3, Name = "next", Tags = new Dictionary<string, int>() },
            Tags = new Dictionary<string, int> { ["ak"] = 7, ["Readyk"] = 1 },
            When = new DateTime(2024, 3, 10, 7, 30, 0, DateTimeKind.Utc), Optional = 9,
            Numbers = new List<int> { 1, 3, 7 }, Contract = new FuzzContract { Value = 11 }
        };
        var document = mapper.ToDocument(row);
        var translated = 0;
        while (context.Next())
        {
            var shapeSeed = unchecked(context.Seed + context.Steps - 1);
            var valueSeed = Math.Abs(unchecked(shapeSeed * 397)) % 7;
            var query = new LinqCacheFuzzGenerator(shapeSeed, valueSeed)
                .Build(Math.Abs(shapeSeed % LinqCacheFuzzGenerator.Kinds));
            context.Trace("linq", new { shapeSeed, valueSeed, expression = query.ToString() });
            if (AssertParity(context, mapper, query, document, row, $"shape={shapeSeed}, value={valueSeed}")) translated++;
            if (query is Expression<Func<FuzzRow, int>> index)
                AssertIndexParity(context, mapper, index, $"index shape={shapeSeed}, value={valueSeed}");
        }

        var errors = new ConcurrentQueue<Exception>();
        var concurrencyCases = Math.Min(256, Math.Max(32, context.Count));
        await Task.WhenAll(Enumerable.Range(0, Math.Min(Environment.ProcessorCount * 2, 12)).Select(worker => Task.Run(() =>
        {
            for (var i = worker; i < concurrencyCases; i += Math.Min(Environment.ProcessorCount * 2, 12))
            {
                try
                {
                    var query = new LinqCacheFuzzGenerator(unchecked(context.Seed + i), worker % 5)
                        .Build(Math.Abs((context.Seed + i) % LinqCacheFuzzGenerator.Kinds));
                    AssertParity(context, mapper, query, document, row, $"concurrent worker={worker}, case={i}");
                }
                catch (Exception error) { errors.Enqueue(error); }
            }
        })));
        context.Check(errors.IsEmpty, "Concurrent LINQ cache mismatch: " + (errors.TryPeek(out var first) ? first : null));
        context.Check(translated > context.Steps / 3, "LINQ grammar produced too few supported translations.");
        context.Metrics["supportedTranslations"] = translated;
        context.Metrics["cacheEntries"] = mapper.LinqExpressionCacheCount;
        context.Metrics["concurrencyCases"] = concurrencyCases;
    }

    private static bool AssertParity(FuzzContext context, BsonMapper mapper, LambdaExpression query,
        BsonDocument document, FuzzRow row, string label)
    {
        var cached = Capture(() => Translate(mapper, query), out var cachedError);
        var fresh = Capture(() => TranslateFresh(new BsonMapper { EnumAsInteger = mapper.EnumAsInteger }, query), out var freshError);
        context.Check(cachedError?.GetType() == freshError?.GetType(),
            $"{label}: cached error {cachedError?.GetType()} != fresh error {freshError?.GetType()}");
        if (cachedError != null) return false;
        context.Check(cached.Source == fresh.Source, $"{label}: source mismatch {cached.Source} != {fresh.Source}");
        context.Check(cached.Type == fresh.Type && cached.IsImmutable == fresh.IsImmutable &&
            cached.IsVolatile == fresh.IsVolatile && cached.IsScalar == fresh.IsScalar && cached.IsANY == fresh.IsANY,
            $"{label}: expression metadata mismatch");
        context.Check(cached.Fields.SetEquals(fresh.Fields), $"{label}: field metadata mismatch");
        context.Check(cached.Parameters.ToString() == fresh.Parameters.ToString(), $"{label}: parameter mismatch");
        var cachedValue = Capture(() => cached.ExecuteScalar(document, Collation.Binary), out var cachedRun);
        var freshValue = Capture(() => fresh.ExecuteScalar(document, Collation.Binary), out var freshRun);
        context.Check(cachedRun?.GetType() == freshRun?.GetType(), $"{label}: execution error mismatch");
        if (cachedRun == null) FuzzOracle.VerifyBsonValue(context, cachedValue, freshValue,
            $"{label}: cached value {cachedValue} != fresh value {freshValue}");

        if (cachedRun == null && (query.ReturnType == typeof(bool) || query.ReturnType == typeof(int)) && !ContainsNewArray(query))
        {
            var clr = Capture(() => query.Compile().DynamicInvoke(row), out var clrError);
            if (clrError == null)
            {
                var expected = mapper.Serialize(query.ReturnType, clr);
                context.Check(cachedValue.Equals(expected), $"{label}: LiteDB value {cachedValue} != CLR value {expected}");
            }
        }
        return true;
    }

    private static bool ContainsNewArray(Expression expression)
    {
        var visitor = new NewArrayVisitor();
        visitor.Visit(expression);
        return visitor.Found;
    }

    private static BsonExpression Translate(BsonMapper mapper, LambdaExpression query) => query switch
    {
        Expression<Func<FuzzRow, bool>> value => mapper.GetExpression(value),
        Expression<Func<FuzzRow, int>> value => mapper.GetExpression(value),
        Expression<Func<FuzzRow, object[]>> value => mapper.GetExpression(value),
        _ => mapper.GetExpression((Expression<Func<FuzzRow, FuzzRow>>)query)
    };

    private static BsonExpression TranslateFresh(BsonMapper mapper, LambdaExpression query)
    {
        var previous = BsonExpression.DisableCompilationCache;
        BsonExpression.DisableCompilationCache = true;
        try { return Translate(mapper, query); }
        finally { BsonExpression.DisableCompilationCache = previous; }
    }

    private static void AssertIndexParity(FuzzContext context, BsonMapper mapper,
        Expression<Func<FuzzRow, int>> query, string label)
    {
        var cached = Capture(() => mapper.GetIndexExpression(query), out var cachedError);
        BsonExpression fresh;
        Exception freshError;
        var previous = BsonExpression.DisableCompilationCache;
        BsonExpression.DisableCompilationCache = true;
        try { fresh = Capture(() => new BsonMapper { EnumAsInteger = mapper.EnumAsInteger }.GetIndexExpression(query), out freshError); }
        finally { BsonExpression.DisableCompilationCache = previous; }
        context.Check(cachedError?.GetType() == freshError?.GetType(), $"{label}: GetIndexExpression error mismatch");
        if (cachedError == null) context.Check(cached.Source == fresh.Source && cached.Parameters.ToString() == fresh.Parameters.ToString(),
            $"{label}: GetIndexExpression cache mismatch");
    }

    private static T Capture<T>(Func<T> action, out Exception error)
    {
        try { error = null; return action(); }
        catch (Exception caught) { error = caught; return default; }
    }

    private sealed class NewArrayVisitor : ExpressionVisitor
    {
        internal bool Found { get; private set; }
        protected override Expression VisitNewArray(NewArrayExpression node)
        {
            Found = true;
            return node;
        }
    }
}
