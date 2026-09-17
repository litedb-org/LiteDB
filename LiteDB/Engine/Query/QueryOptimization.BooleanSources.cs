using System.Collections.Generic;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private Dictionary<BsonExpression, BsonExpression> _booleanSources;
        private bool? _hasBooleanTerms;

        private BsonExpression NormalizeContainsTerm(BsonExpression term)
        {
            var normalized = BsonExpressionFactory.NormalizeContains(term);
            if (_terms.Count < 2) return normalized;
            if (!_hasBooleanTerms.HasValue) _hasBooleanTerms = _terms.Exists(x => x.Type == BsonExpressionType.Or);
            if (_hasBooleanTerms.Value)
            {
                // The rewrite invokes compiled delegates with a new context. Keep
                // its original structural proof and bindings for this query only.
                if (_booleanSources == null) _booleanSources = new Dictionary<BsonExpression, BsonExpression>();
                _booleanSources.Add(normalized, term);
            }
            return normalized;
        }

        private BsonExpression BooleanSource(BsonExpression term) =>
            _booleanSources != null && _booleanSources.TryGetValue(term, out var source) ? source : term;
    }
}
