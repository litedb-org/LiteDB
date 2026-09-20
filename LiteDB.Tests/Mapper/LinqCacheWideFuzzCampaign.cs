using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace LiteDB.Tests.Mapper
{
    internal sealed class LinqCacheWideFuzzCampaign
    {
        private const int ValueSets = 3;
        private const int SampleLimit = 10000;

        private readonly int _start;
        private readonly int _shapes;
        private readonly int _configuration;
        private readonly int _threads;
        private readonly string _output;
        private readonly bool _enumAsInteger;
        private readonly bool _indexExpression;
        private readonly BsonMapper _mapper;
        private readonly ConcurrentQueue<Failure> _samples = new ConcurrentQueue<Failure>();
        private long _translated;
        private long _rejected;
        private long _cacheFailures;
        private long _translatorDifferences;
        private long _generatorFailures;

        internal LinqCacheWideFuzzCampaign(int start, int shapes, int configuration, int threads, string output)
        {
            _start = start;
            _shapes = shapes;
            _configuration = configuration;
            _threads = threads;
            _output = output;
            _enumAsInteger = (configuration & 1) != 0;
            _indexExpression = (configuration & 2) != 0;
            _mapper = new BsonMapper { EnumAsInteger = _enumAsInteger };
        }

        internal CampaignSummary Run()
        {
            var rows = Rows();
            var documents = Documents(_mapper, rows);
            var options = new ParallelOptions { MaxDegreeOfParallelism = _threads };
            Parallel.For(_start, _start + _shapes, options, seed => RunSeed(seed, rows, documents));
            var summary = new CampaignSummary
            {
                Start = _start, Shapes = _shapes, Configuration = _configuration, Threads = _threads,
                EnumAsInteger = _enumAsInteger, IndexExpression = _indexExpression,
                Translated = _translated, Rejected = _rejected, CacheFailures = _cacheFailures,
                TranslatorDifferences = _translatorDifferences, GeneratorFailures = _generatorFailures,
                CacheEntries = _mapper.LinqExpressionCacheCount
            };
            Write(summary);
            return summary;
        }

        private void RunSeed(int seed, WideFuzzRow[] rows, BsonDocument[] documents)
        {
            var order = seed % 2 == 0 ? new[] { 0, 1, 2 } : new[] { 2, 1, 0 };
            foreach (var valueSeed in order)
            {
                LambdaExpression query;
                try { query = new LinqCacheWideFuzzGenerator(seed, valueSeed).Build(seed % LinqCacheWideFuzzGenerator.Kinds); }
                catch (Exception exception)
                {
                    Record("generator", seed, valueSeed, null, exception.GetType().FullName + ": " + exception.Message);
                    continue;
                }

                BsonExpression cached;
                BsonExpression direct;
                Exception cachedError;
                Exception directError;
                cached = Capture(() => TranslateCached(_mapper, query), out cachedError);
                var directMapper = new BsonMapper { EnumAsInteger = _enumAsInteger };
                direct = Capture(() => TranslateDirect(directMapper, query), out directError);
                if (cachedError != null || directError != null)
                {
                    if (SameError(cachedError, directError)) Interlocked.Increment(ref _rejected);
                    else Record("cache", seed, valueSeed, query, ErrorPair(cachedError, directError));
                    continue;
                }

                Interlocked.Increment(ref _translated);
                var difference = MetadataDifference(cached, direct, "root") ??
                    (cached.Parameters.ToString() == direct.Parameters.ToString() ? null :
                        $"parameters: cached={cached.Parameters}, direct={direct.Parameters}");
                if (difference != null)
                {
                    Record("cache", seed, valueSeed, query, difference);
                    continue;
                }

                for (var i = 0; i < documents.Length; i++)
                {
                    var actual = Capture(() => cached.ExecuteScalar(documents[i], Collation.Binary), out cachedError);
                    var expected = Capture(() => direct.ExecuteScalar(documents[i], Collation.Binary), out directError);
                    if (!SameResult(actual, cachedError, expected, directError))
                    {
                        Record("cache", seed, valueSeed, query,
                            $"document {i}: cached={Display(actual, cachedError)}, direct={Display(expected, directError)}");
                        break;
                    }
                }

                // A separately compiled CLR lambda checks the translator, not just reuse.
                // Missing-field documents have no CLR equivalent and are checked above only.
                var compiled = Capture(() => query.Compile(), out var compileError);
                if (compileError != null)
                {
                    Record("generator", seed, valueSeed, query, "compile: " + Display(null, compileError));
                    continue;
                }
                for (var i = 0; i < rows.Length; i++) CompareClr(seed, valueSeed, query, compiled, rows[i], documents[i], cached);
            }
        }

        private void CompareClr(int seed, int valueSeed, LambdaExpression query, Delegate compiled,
            WideFuzzRow row, BsonDocument document, BsonExpression translated)
        {
            // LINQ member initializers are BSON projections: omitted CLR members are
            // absent, whereas serializing the compiled object's defaults adds them.
            if (query.ReturnType == typeof(WideFuzzProjection)) return;
            var clr = Capture(() => compiled.DynamicInvoke(row), out var clrError);
            var bson = Capture(() => translated.ExecuteScalar(document, Collation.Binary), out var bsonError);
            if (clrError is TargetInvocationException invocation) clrError = invocation.InnerException;
            BsonValue expected = null;
            Exception serializeError = null;
            if (clrError == null) expected = Capture(() => SerializeClr(clr, query.ReturnType), out serializeError);
            clrError = clrError ?? serializeError;
            if (!SameResult(bson, bsonError, expected, clrError))
                Record("translator", seed, valueSeed, query,
                    $"CLR row {row.Id}: bson={Display(bson, bsonError)}, clr={Display(expected, clrError)}");
        }

        private BsonExpression TranslateDirect(BsonMapper mapper, LambdaExpression query) =>
            new LinqExpressionTranslator(mapper, query).Resolve(!_indexExpression && query.ReturnType == typeof(bool));

        private BsonExpression TranslateCached(BsonMapper mapper, LambdaExpression query)
        {
            if (query is Expression<Func<WideFuzzRow, bool>> cb) return _indexExpression ? mapper.GetIndexExpression(cb) : mapper.GetExpression(cb);
            if (query is Expression<Func<WideFuzzRow, int>> ci) return _indexExpression ? mapper.GetIndexExpression(ci) : mapper.GetExpression(ci);
            if (query is Expression<Func<WideFuzzRow, object[]>> ca) return _indexExpression ? mapper.GetIndexExpression(ca) : mapper.GetExpression(ca);
            if (query is Expression<Func<WideFuzzRow, DateTime>> cd) return _indexExpression ? mapper.GetIndexExpression(cd) : mapper.GetExpression(cd);
            if (query is Expression<Func<WideFuzzRow, int[]>> cis) return _indexExpression ? mapper.GetIndexExpression(cis) : mapper.GetExpression(cis);
            if (query is Expression<Func<WideFuzzRow, WideFuzzProjection>> cp) return _indexExpression ? mapper.GetIndexExpression(cp) : mapper.GetExpression(cp);
            if (query is Expression<Func<IWideFuzzRow, bool>> ib) return _indexExpression ? mapper.GetIndexExpression(ib) : mapper.GetExpression(ib);
            if (query is Expression<Func<IWideFuzzRow, int>> ii) return _indexExpression ? mapper.GetIndexExpression(ii) : mapper.GetExpression(ii);
            if (query is Expression<Func<IWideFuzzRow, object[]>> ia) return _indexExpression ? mapper.GetIndexExpression(ia) : mapper.GetExpression(ia);
            if (query is Expression<Func<IWideFuzzRow, DateTime>> id) return _indexExpression ? mapper.GetIndexExpression(id) : mapper.GetExpression(id);
            if (query is Expression<Func<IWideFuzzRow, int[]>> iis) return _indexExpression ? mapper.GetIndexExpression(iis) : mapper.GetExpression(iis);
            if (query is Expression<Func<IWideFuzzRow, WideFuzzProjection>> ip) return _indexExpression ? mapper.GetIndexExpression(ip) : mapper.GetExpression(ip);
            throw new InvalidOperationException("Unexpected lambda type: " + query.Type);
        }

        private void Record(string kind, int seed, int valueSeed, LambdaExpression query, string detail)
        {
            if (kind == "cache") Interlocked.Increment(ref _cacheFailures);
            else if (kind == "translator") Interlocked.Increment(ref _translatorDifferences);
            else Interlocked.Increment(ref _generatorFailures);
            if (_samples.Count < SampleLimit) _samples.Enqueue(new Failure
            {
                Kind = kind, Seed = seed, ValueSeed = valueSeed,
                Expression = query?.ToString(), Detail = detail
            });
        }

        private void Write(CampaignSummary summary)
        {
            if (string.IsNullOrWhiteSpace(_output)) return;
            using var writer = new StreamWriter(_output, false);
            writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary));
            foreach (var failure in _samples.OrderBy(x => x.Seed).ThenBy(x => x.ValueSeed).ThenBy(x => x.Kind))
                writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(failure));
        }

        private static WideFuzzRow[] Rows() => new[]
        {
            // Engine reads expose stored dates in the process-local zone; use that
            // same representation for the independently compiled CLR reference.
            Row(1, 7, 3, "Ready", new[] { 1, 7, 12 }, new DateTime(2020, 3, 29, 0, 30, 0, DateTimeKind.Local)),
            Row(2, -2, 0, "ready", Array.Empty<int>(), new DateTime(2021, 10, 31, 1, 30, 0, DateTimeKind.Local)),
            Row(3, 42, 17, "zz", new[] { -5, 0, 20 }, new DateTime(2024, 2, 29, 12, 0, 0, DateTimeKind.Local))
        };

        private static WideFuzzRow Row(int id, int value, int optional, string name, int[] items, DateTime when) =>
            new WideFuzzRow { Id = id, Value = value, Optional = optional, Name = name, Items = items, When = when,
                Next = new WideFuzzRow { Id = id + 100, Value = value + 1, Name = name, Items = items, When = when },
                Tags = new Dictionary<string, int> { ["ak"] = value, ["Readyk"] = optional } };

        private static BsonDocument[] Documents(BsonMapper mapper, WideFuzzRow[] rows)
        {
            var documents = rows.Select(mapper.ToDocument).ToList();
            var nulls = new BsonDocument { ["_id"] = 4, ["Value"] = 0, ["Optional"] = BsonValue.Null,
                ["Name"] = BsonValue.Null, ["When"] = BsonValue.Null, ["Items"] = BsonValue.Null,
                ["Next"] = BsonValue.Null, ["Tags"] = new BsonDocument() };
            documents.Add(nulls);
            documents.Add(new BsonDocument { ["_id"] = 5 });
            return documents.ToArray();
        }

        private BsonValue SerializeClr(object value, Type declaredType)
        {
            if (value == null) return BsonValue.Null;
            if (value is BsonValue bson) return bson;
            // LINQ bindings deliberately preserve empty strings regardless of the
            // mapper's document-serialization EmptyStringToNull option.
            if (value is string text) return new BsonValue(text);
            if (value is Array array)
            {
                var result = new BsonArray();
                foreach (var item in array) result.Add(SerializeClr(item, item?.GetType() ?? typeof(object)));
                return result;
            }
            return _mapper.Serialize(declaredType, value);
        }

        private static string MetadataDifference(BsonExpression x, BsonExpression y, string path)
        {
            if (x.Source != y.Source || x.Type != y.Type || x.IsScalar != y.IsScalar || x.IsImmutable != y.IsImmutable ||
                x.IsVolatile != y.IsVolatile || x.IsANY != y.IsANY || x.UseSource != y.UseSource || !x.Fields.SetEquals(y.Fields))
                return $"{path} metadata: cached={x.Source}, direct={y.Source}";
            if ((x.Left == null) != (y.Left == null) || (x.Right == null) != (y.Right == null)) return path + " child presence";
            return x.Left != null ? MetadataDifference(x.Left, y.Left, path + ".left") ??
                (x.Right == null ? null : MetadataDifference(x.Right, y.Right, path + ".right")) :
                x.Right == null ? null : MetadataDifference(x.Right, y.Right, path + ".right");
        }

        private static bool SameResult(BsonValue x, Exception xe, BsonValue y, Exception ye) =>
            SameError(xe, ye) && (xe != null || Equals(x, y));
        private static bool SameError(Exception x, Exception y) => x?.GetType() == y?.GetType();
        private static string ErrorPair(Exception x, Exception y) => $"cached={Display(null, x)}, direct={Display(null, y)}";
        private static string Display(BsonValue value, Exception error) => error == null ? value?.ToString() ?? "<null>" : error.GetType().FullName + ": " + error.Message;
        private static T Capture<T>(Func<T> action, out Exception error)
        {
            error = null;
            try { return action(); }
            catch (Exception exception) { error = exception; return default(T); }
        }

        internal sealed class CampaignSummary
        {
            public int Start { get; set; }
            public int Shapes { get; set; }
            public int Configuration { get; set; }
            public int Threads { get; set; }
            public bool EnumAsInteger { get; set; }
            public bool IndexExpression { get; set; }
            public long Translated { get; set; }
            public long Rejected { get; set; }
            public long CacheFailures { get; set; }
            public long TranslatorDifferences { get; set; }
            public long GeneratorFailures { get; set; }
            public int CacheEntries { get; set; }
        }

        private sealed class Failure
        {
            public string Kind { get; set; }
            public int Seed { get; set; }
            public int ValueSeed { get; set; }
            public string Expression { get; set; }
            public string Detail { get; set; }
        }
    }
}
