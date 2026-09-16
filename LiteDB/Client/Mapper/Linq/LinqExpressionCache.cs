using System;
using System.Linq.Expressions;
using System.Threading;

namespace LiteDB
{
    internal sealed class LinqExpressionCache
    {
        private readonly Entry[] _entries = new Entry[256];
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
            var bucket = (int)((uint)shape.Hash % (uint)_entries.Length);
            var cached = Volatile.Read(ref _entries[bucket]);
            if (cached != null && cached.Matches(shape, mapper.EnumAsInteger, predicate))
            {
                try { return cached.Bind(mapper, shape); }
                catch (Exception exception)
                {
                    throw new NotSupportedException($"Invalid BsonExpression when converted from Linq expression: {expression} - {exception.Message}", exception);
                }
            }
            var translator = new LinqExpressionTranslator(mapper, expression, true);
            var result = translator.Resolve(predicate);
            var slots = new int[translator.Bindings.Count];
            for (var i = 0; i < slots.Length; i++)
            {
                slots[i] = shape.Expressions.IndexOf(translator.Bindings[i]);
                // Some translations synthesize bindings (enum names, DbRef metadata,
                // invoked lambdas). Keep their existing translation path.
                if (slots[i] < 0 || translator.Bindings[i] == null) return result;
            }
            var entry = new Entry(shape.Tokens.ToArray(), slots, translator.MemberGuards.ToArray(),
                result.Bind(new BsonDocument()), mapper.EnumAsInteger, predicate);
            if (Interlocked.Exchange(ref _entries[bucket], entry) == null) Interlocked.Increment(ref _count);
            return result;
        }

        private sealed class Entry
        {
            private readonly LinqQueryShape.Token[] _tokens;
            private readonly int[] _slots;
            private readonly string[] _names;
            private readonly LinqMemberGuard[] _members;
            private readonly BsonExpression _template;
            private readonly bool _enumAsInteger;
            private readonly bool _predicate;

            internal Entry(LinqQueryShape.Token[] tokens, int[] slots, LinqMemberGuard[] members,
                BsonExpression template, bool enumAsInteger, bool predicate)
            {
                _tokens = tokens;
                _slots = slots;
                _members = members;
                _template = template;
                _enumAsInteger = enumAsInteger;
                _predicate = predicate;
                _names = new string[slots.Length];
                for (var i = 0; i < slots.Length; i++) _names[i] = "p" + i;
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
                    var value = LinqExpressionTranslator.Evaluate(shape.Expressions[_slots[i]]);
                    parameters[_names[i]] = value == null ? BsonValue.Null : value is string text ?
                        new BsonValue(text) : mapper.Serialize(value.GetType(), value);
                }
                return _template.Bind(parameters);
            }
        }
    }
}
