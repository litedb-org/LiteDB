using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB.Engine
{
    /// <summary>
    /// Compiles direct-field projections so the result document can be built
    /// without first creating an owning source document.
    /// </summary>
    internal sealed class BorrowedProjectionEvaluator
    {
        private readonly string[] _names;
        private readonly int[] _slots;
        private readonly bool _isProjectionValue;

        private BorrowedProjectionEvaluator(BorrowedFieldPath[] paths,
            string[] names, int[] slots, bool isProjectionValue)
        {
            this.Paths = paths;
            _names = names;
            _slots = slots;
            _isProjectionValue = isProjectionValue;
        }

        public BorrowedFieldPath[] Paths { get; }

        public int SlotCount => this.Paths.Length;

        public static bool TryCreate(BsonExpression expression,
            out BorrowedProjectionEvaluator evaluator)
        {
            if (expression.Type == BsonExpressionType.Path && expression.IsScalar &&
                TryAddPath(expression.Expression, new List<BorrowedFieldPath>(),
                    out var path, out var slot))
            {
                evaluator = new BorrowedProjectionEvaluator(new[] { path },
                    new[] { expression.DefaultFieldName() }, new[] { slot }, true);
                return true;
            }

            if (expression.Type != BsonExpressionType.Document ||
                !(expression.Expression is MethodCallExpression call) ||
                call.Method.DeclaringType != typeof(BsonExpressionOperators) ||
                call.Method.Name != "DOCUMENT_INIT" || call.Arguments.Count != 2 ||
                !(call.Arguments[0] is NewArrayExpression keys) ||
                !(call.Arguments[1] is NewArrayExpression values) ||
                keys.Expressions.Count != values.Expressions.Count)
            {
                evaluator = null;
                return false;
            }

            var paths = new List<BorrowedFieldPath>();
            var names = new string[keys.Expressions.Count];
            var slots = new int[names.Length];

            for (var i = 0; i < names.Length; i++)
            {
                if (!(keys.Expressions[i] is ConstantExpression key) ||
                    !(key.Value is string name) ||
                    !TryAddPath(values.Expressions[i], paths, out _, out slots[i]))
                {
                    evaluator = null;
                    return false;
                }

                names[i] = name;
            }

            evaluator = new BorrowedProjectionEvaluator(paths.ToArray(), names, slots, false);
            return paths.Count > 0;
        }

        public bool TryProject(BorrowedValueBuffer source, out BsonDocument document)
        {
            document = new BsonDocument { IsProjectionValue = _isProjectionValue };

            for (var i = 0; i < _slots.Length; i++)
            {
                if (!source[_slots[i]].TryMaterialize(out var value))
                {
                    document = null;
                    return false;
                }

                document[_names[i]] = value;
            }

            return true;
        }

        private static bool TryAddPath(Expression expression,
            List<BorrowedFieldPath> paths, out BorrowedFieldPath path, out int slot)
        {
            if (!BorrowedPredicateEvaluator.TryExtractPath(expression, out var segments))
            {
                path = null;
                slot = -1;
                return false;
            }

            for (var i = 0; i < paths.Count; i++)
            {
                if (paths[i].HasSameSegments(segments))
                {
                    path = paths[i];
                    slot = i;
                    return true;
                }
            }

            if (paths.Count == 64)
            {
                path = null;
                slot = -1;
                return false;
            }

            if (!BorrowedFieldPath.TryCreate(segments, paths.Count, out path))
            {
                slot = -1;
                return false;
            }

            slot = paths.Count;
            paths.Add(path);
            return true;
        }
    }
}
