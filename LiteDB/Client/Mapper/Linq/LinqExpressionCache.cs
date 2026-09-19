using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;

namespace LiteDB
{
    internal sealed class LinqExpressionCache
    {
        // Four templates per bucket reduce collision churn at the same 256-entry
        // bound. Published buckets are immutable, and hits allocate no cache state.
        private readonly Entry[][] _buckets = new Entry[64][];
        private int _count;
        internal int Count => Volatile.Read(ref _count);

        internal BsonExpression Resolve(BsonMapper mapper, LambdaExpression expression, bool predicate)
        {
#if TESTING
            if (BsonExpression.DisableCompilationCache) return new LinqExpressionTranslator(mapper, expression).Resolve(predicate);
#endif
            var shape = LinqQueryShape.Rent();
            try
            {
                shape.Visit(expression);
                return Resolve(mapper, expression, predicate, shape);
            }
            finally { shape.Release(); }
        }

        private BsonExpression Resolve(BsonMapper mapper, LambdaExpression expression, bool predicate, LinqQueryShape shape)
        {
            if (!shape.Supported) return new LinqExpressionTranslator(mapper, expression).Resolve(predicate);
            var bucket = GetBucket(shape.Hash);
            var entries = Volatile.Read(ref _buckets[bucket]);
            if (entries != null)
            {
                foreach (var cached in entries)
                {
                    if (!cached.Matches(shape, mapper.EnumAsInteger, predicate)) continue;
                    try { return cached.Bind(mapper, shape); }
                    catch (Exception exception)
                    {
                        throw new NotSupportedException($"Invalid BsonExpression when converted from Linq expression: {expression} - {exception.Message}", exception);
                    }
                }
            }
            var translator = new LinqExpressionTranslator(mapper, expression, true);
            var result = translator.Resolve(predicate);
            var slots = new int[translator.Bindings.Count];
            for (var i = 0; i < slots.Length; i++)
            {
                slots[i] = shape.Expressions.IndexOf(translator.Bindings[i]);
                // Some translations synthesize bindings (enum names, DbRef metadata,
                // invoked lambdas), or reuse one binding node at multiple positions.
                // Keep their existing translation path rather than aliasing slots.
                if (slots[i] < 0 || translator.Bindings[i] == null ||
                    shape.Expressions.IndexOf(translator.Bindings[i], slots[i] + 1) >= 0) return result;
            }
            var entry = new Entry(shape.Tokens.ToArray(), slots, translator.MemberGuards.ToArray(),
                result.Bind(new BsonDocument()), mapper.EnumAsInteger, predicate, shape);
            Publish(bucket, entry, shape, mapper.EnumAsInteger, predicate);
            return result;
        }

        internal static int GetBucket(int hash)
        {
            // Polynomial shape hashes have patterned low bits for repeated nodes.
            // Mix all bits before selecting a power-of-two bucket.
            var value = unchecked((uint)hash);
            value ^= value >> 16;
            value = unchecked(value * 0x7feb352dU);
            value ^= value >> 15;
            return (int)(value & 63);
        }

        private void Publish(int bucket, Entry entry, LinqQueryShape shape, bool enumAsInteger, bool predicate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _buckets[bucket]);
                // A concurrent caller may already have published this translation.
                if (current != null)
                    foreach (var cached in current)
                        if (cached.Matches(shape, enumAsInteger, predicate)) return;
                var length = current?.Length ?? 0;
                var replacement = new Entry[Math.Min(4, length + 1)];
                replacement[0] = entry;
                if (length != 0) Array.Copy(current, 0, replacement, 1, replacement.Length - 1);
                if (!ReferenceEquals(Interlocked.CompareExchange(ref _buckets[bucket], replacement, current), current)) continue;
                if (length < 4) Interlocked.Increment(ref _count);
                return;
            }
        }

        private sealed class Entry
        {
            private readonly LinqQueryShape.Token[] _tokens;
            private readonly int[] _slots;
            private readonly string[] _names;
            private readonly Lazy<Func<List<Expression>, object>>[] _evaluators;
            private readonly LinqMemberGuard[] _members;
            private readonly BsonExpression _template;
            private readonly bool _enumAsInteger;
            private readonly bool _predicate;

            internal Entry(LinqQueryShape.Token[] tokens, int[] slots, LinqMemberGuard[] members,
                BsonExpression template, bool enumAsInteger, bool predicate, LinqQueryShape shape)
            {
                _tokens = tokens;
                _slots = slots;
                _members = members;
                _template = template;
                _enumAsInteger = enumAsInteger;
                _predicate = predicate;
                _names = new string[slots.Length];
                _evaluators = new Lazy<Func<List<Expression>, object>>[slots.Length];
                for (var i = 0; i < slots.Length; i++)
                {
                    _names[i] = "p" + i;
                    _evaluators[i] = LinqBindingEvaluator.Create(shape.Expressions[slots[i]], slots[i]);
                }
            }

            internal bool Matches(LinqQueryShape shape, bool enumAsInteger, bool predicate)
            {
                if (_enumAsInteger != enumAsInteger || _predicate != predicate || _tokens.Length != shape.Tokens.Count) return false;
                for (var i = 0; i < _tokens.Length; i++) if (!_tokens[i].Equals(shape.Tokens[i])) return false;
                foreach (var member in _members) if (!member.IsCurrent()) return false;
                return true;
            }

            internal BsonExpression Bind(BsonMapper mapper, LinqQueryShape shape)
            {
                var parameters = new BsonDocument();
                // Reevaluate each slot in translation order, including repeated slots;
                // never retain a caller's closure or reuse its serialized values.
                for (var i = 0; i < _slots.Length; i++)
                {
                    var value = _evaluators[i] == null ? LinqExpressionTranslator.Evaluate(shape.Expressions[_slots[i]]) :
                        _evaluators[i].Value(shape.Expressions);
                    parameters[_names[i]] = value == null ? BsonValue.Null : value is string text ?
                        new BsonValue(text) : mapper.Serialize(value.GetType(), value);
                }
                return _template.Bind(parameters);
            }
        }
    }
}
