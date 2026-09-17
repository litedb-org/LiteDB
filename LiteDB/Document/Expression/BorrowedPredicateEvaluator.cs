using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB.Engine
{
    /// <summary>
    /// Compiles the scalar subset of a BSON predicate into an evaluator over
    /// borrowed field slots. Compilation inspects the parsed expression tree;
    /// it never dispatches on expression source text.
    /// </summary>
    internal sealed class BorrowedPredicateEvaluator
    {
        private readonly PredicateNode[] _predicates;

        private BorrowedPredicateEvaluator(BorrowedFieldPath[] paths, PredicateNode[] predicates)
        {
            this.Paths = paths;
            _predicates = predicates;
        }

        public BorrowedFieldPath[] Paths { get; }

        public int SlotCount => this.Paths.Length;

        public static bool TryCreate(IReadOnlyList<BsonExpression> expressions,
            out BorrowedPredicateEvaluator evaluator)
        {
            var paths = new List<BorrowedFieldPath>();
            var predicates = new PredicateNode[expressions.Count];

            for (var i = 0; i < expressions.Count; i++)
            {
                if (!TryBuildPredicate(expressions[i], paths, out predicates[i]))
                {
                    evaluator = null;
                    return false;
                }
            }

            evaluator = new BorrowedPredicateEvaluator(paths.ToArray(), predicates);
            return paths.Count > 0;
        }

        public bool TryEvaluate(BorrowedBsonValue[] values, Collation collation, out bool result)
        {
            for (var i = 0; i < _predicates.Length; i++)
            {
                if (!_predicates[i].TryEvaluate(values, collation, out var matches))
                {
                    result = false;
                    return false;
                }

                if (!matches)
                {
                    result = false;
                    return true;
                }
            }

            result = true;
            return true;
        }

        private static bool TryBuildPredicate(BsonExpression expression,
            List<BorrowedFieldPath> paths, out PredicateNode predicate)
        {
            if (expression.Type == BsonExpressionType.And || expression.Type == BsonExpressionType.Or)
            {
                if (!TryBuildPredicate(expression.Left, paths, out var left) ||
                    !TryBuildPredicate(expression.Right, paths, out var right))
                {
                    predicate = null;
                    return false;
                }

                predicate = new LogicNode(expression.Type, left, right);
                return true;
            }

            switch (expression.Type)
            {
                case BsonExpressionType.Equal:
                case BsonExpressionType.NotEqual:
                case BsonExpressionType.GreaterThan:
                case BsonExpressionType.GreaterThanOrEqual:
                case BsonExpressionType.LessThan:
                case BsonExpressionType.LessThanOrEqual:
                    if (TryBuildValue(expression.Left, paths, out var leftValue) &&
                        TryBuildValue(expression.Right, paths, out var rightValue))
                    {
                        predicate = new ComparisonNode(expression.Type, leftValue, rightValue);
                        return true;
                    }
                    break;
            }

            predicate = null;
            return false;
        }

        private static bool TryBuildValue(BsonExpression expression,
            List<BorrowedFieldPath> paths, out ValueNode value)
        {
            if (expression == null || !expression.IsScalar)
            {
                value = null;
                return false;
            }

            if (expression.Type == BsonExpressionType.Path &&
                TryExtractPath(expression.Expression, out var segments))
            {
                var slot = FindPath(paths, segments);

                if (slot < 0)
                {
                    if (paths.Count == 64 || !BorrowedFieldPath.TryCreate(segments, paths.Count, out var path))
                    {
                        value = null;
                        return false;
                    }

                    slot = paths.Count;
                    paths.Add(path);
                }

                value = new PathNode(slot);
                return true;
            }

            if (expression.Type == BsonExpressionType.Parameter &&
                TryExtractParameter(expression.Expression, out var parameterName))
            {
                value = new ParameterNode(expression.Parameters, parameterName);
                return true;
            }

            if (expression.Expression is ConstantExpression constant && constant.Value is BsonValue bsonValue)
            {
                BorrowedBsonValue.TryFromOwned(bsonValue, out var borrowed);
                value = new ConstantNode(borrowed);
                return true;
            }

            value = null;
            return false;
        }

        private static int FindPath(List<BorrowedFieldPath> paths, string[] segments)
        {
            for (var i = 0; i < paths.Count; i++)
            {
                if (paths[i].HasSameSegments(segments)) return i;
            }

            return -1;
        }

        internal static bool TryExtractPath(Expression expression, out string[] segments)
        {
            var reverse = new List<string>();
            var current = expression;

            while (current is MethodCallExpression call &&
                call.Method.DeclaringType == typeof(BsonExpressionOperators) &&
                call.Method.Name == "MEMBER_PATH" && call.Arguments.Count == 2 &&
                call.Arguments[1] is ConstantExpression name && name.Value is string field)
            {
                if (string.IsNullOrEmpty(field))
                {
                    segments = null;
                    return false;
                }

                reverse.Add(field);
                current = call.Arguments[0];
            }

            if (!(current is ParameterExpression parameter) || parameter.Type != typeof(BsonDocument))
            {
                segments = null;
                return false;
            }

            reverse.Reverse();
            segments = reverse.ToArray();
            return segments.Length > 0;
        }

        private static bool TryExtractParameter(Expression expression, out string name)
        {
            if (expression is MethodCallExpression call &&
                call.Method.DeclaringType == typeof(BsonExpressionOperators) &&
                call.Method.Name == "PARAMETER_PATH" && call.Arguments.Count == 2 &&
                call.Arguments[1] is ConstantExpression constant && constant.Value is string parameterName)
            {
                name = parameterName;
                return true;
            }

            name = null;
            return false;
        }

        private abstract class PredicateNode
        {
            public abstract bool TryEvaluate(BorrowedBsonValue[] values,
                Collation collation, out bool result);
        }

        private abstract class ValueNode
        {
            public abstract bool TryGetValue(BorrowedBsonValue[] values,
                out BorrowedBsonValue result);
        }

        private sealed class PathNode : ValueNode
        {
            private readonly int _slot;

            public PathNode(int slot) => _slot = slot;

            public override bool TryGetValue(BorrowedBsonValue[] values,
                out BorrowedBsonValue result)
            {
                result = values[_slot];
                return true;
            }
        }

        private sealed class ConstantNode : ValueNode
        {
            private readonly BorrowedBsonValue _value;

            public ConstantNode(BorrowedBsonValue value) => _value = value;

            public override bool TryGetValue(BorrowedBsonValue[] values,
                out BorrowedBsonValue result)
            {
                result = _value;
                return true;
            }
        }

        private sealed class ParameterNode : ValueNode
        {
            private readonly BsonDocument _parameters;
            private readonly string _name;

            public ParameterNode(BsonDocument parameters, string name)
            {
                _parameters = parameters;
                _name = name;
            }

            public override bool TryGetValue(BorrowedBsonValue[] values,
                out BorrowedBsonValue result)
            {
                var owned = _parameters.TryGetValue(_name, out var parameter)
                    ? parameter
                    : BsonValue.Null;

                BorrowedBsonValue.TryFromOwned(owned, out result);
                return true;
            }
        }

        private sealed class ComparisonNode : PredicateNode
        {
            private readonly BsonExpressionType _type;
            private readonly ValueNode _left;
            private readonly ValueNode _right;

            public ComparisonNode(BsonExpressionType type, ValueNode left, ValueNode right)
            {
                _type = type;
                _left = left;
                _right = right;
            }

            public override bool TryEvaluate(BorrowedBsonValue[] values,
                Collation collation, out bool result)
            {
                if (!_left.TryGetValue(values, out var left) ||
                    !_right.TryGetValue(values, out var right))
                {
                    result = false;
                    return false;
                }

                var comparisonCollation = _type == BsonExpressionType.Equal ||
                    _type == BsonExpressionType.NotEqual ? collation : Collation.Binary;

                if (!left.TryCompare(right, comparisonCollation, out var comparison))
                {
                    result = false;
                    return false;
                }

                switch (_type)
                {
                    case BsonExpressionType.Equal: result = comparison == 0; break;
                    case BsonExpressionType.NotEqual: result = comparison != 0; break;
                    case BsonExpressionType.GreaterThan: result = comparison > 0; break;
                    case BsonExpressionType.GreaterThanOrEqual: result = comparison >= 0; break;
                    case BsonExpressionType.LessThan: result = comparison < 0; break;
                    default: result = comparison <= 0; break;
                }

                return true;
            }
        }

        private sealed class LogicNode : PredicateNode
        {
            private readonly BsonExpressionType _type;
            private readonly PredicateNode _left;
            private readonly PredicateNode _right;

            public LogicNode(BsonExpressionType type, PredicateNode left, PredicateNode right)
            {
                _type = type;
                _left = left;
                _right = right;
            }

            public override bool TryEvaluate(BorrowedBsonValue[] values,
                Collation collation, out bool result)
            {
                if (!_left.TryEvaluate(values, collation, out var left))
                {
                    result = false;
                    return false;
                }

                if ((_type == BsonExpressionType.And && !left) ||
                    (_type == BsonExpressionType.Or && left))
                {
                    result = left;
                    return true;
                }

                return _right.TryEvaluate(values, collation, out result);
            }
        }
    }
}
