using System.Collections.Generic;

namespace LiteDB.Engine
{
    /// <summary>
    /// Extracts owning scalar keys from borrowed BSON for operations, such as
    /// sorting, that must retain values beyond the current read scope.
    /// </summary>
    internal sealed class BorrowedScalarEvaluator
    {
        private readonly int[] _slots;

        private BorrowedScalarEvaluator(BorrowedFieldPath[] paths, int[] slots)
        {
            this.Paths = paths;
            _slots = slots;
        }

        public BorrowedFieldPath[] Paths { get; }

        public int SlotCount => this.Paths.Length;

        public int ValueCount => _slots.Length;

        public static bool TryCreate(IReadOnlyList<OrderByItem> expressions,
            out BorrowedScalarEvaluator evaluator)
        {
            var paths = new List<BorrowedFieldPath>();
            var slots = new int[expressions.Count];

            for (var i = 0; i < expressions.Count; i++)
            {
                var expression = expressions[i].Expression;

                if (expression.Type != BsonExpressionType.Path || !expression.IsScalar ||
                    !BorrowedPredicateEvaluator.TryExtractPath(expression.Expression, out var segments))
                {
                    evaluator = null;
                    return false;
                }

                var slot = FindPath(paths, segments);

                if (slot < 0)
                {
                    if (paths.Count == 64 ||
                        !BorrowedFieldPath.TryCreate(segments, paths.Count, out var path))
                    {
                        evaluator = null;
                        return false;
                    }

                    slot = paths.Count;
                    paths.Add(path);
                }

                slots[i] = slot;
            }

            evaluator = new BorrowedScalarEvaluator(paths.ToArray(), slots);
            return paths.Count > 0;
        }

        public bool TryGetValues(BorrowedBsonValue[] source, BsonValue[] values)
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                if (!source[_slots[i]].TryMaterialize(out values[i])) return false;
            }

            return true;
        }

        public bool TryGetValue(BorrowedBsonValue[] source, int index, out BsonValue value)
        {
            return source[_slots[index]].TryMaterialize(out value);
        }

        private static int FindPath(List<BorrowedFieldPath> paths, string[] segments)
        {
            for (var i = 0; i < paths.Count; i++)
            {
                if (paths[i].HasSameSegments(segments)) return i;
            }

            return -1;
        }
    }
}
