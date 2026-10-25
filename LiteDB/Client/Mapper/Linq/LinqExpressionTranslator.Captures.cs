using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using static LiteDB.BsonExpressionFactory;

namespace LiteDB
{
    internal sealed partial class LinqExpressionTranslator
    {
        private BsonExpression TranslateIndex(IndexExpression node)
        {
            ValidateIndexAccess(node.Object, node.Arguments);
            if (node.Arguments.Count != 1)
                throw new NotSupportedException("Multidimensional query arrays are not supported.");
            var target = Translate(node.Object);
            var index = Evaluate(node.Arguments[0], typeof(string), typeof(int));
            if (index is string field)
            {
                if (field.Length == 0) throw new NotSupportedException("Empty row dictionary keys are not supported by BSON paths.");
                return Member(target, field, _scope, _context);
            }
            return Index(target, (int)index, null, _context);
        }

        private static void ValidateIndexAccess(Expression source, IEnumerable<Expression> indexes)
        {
            var arguments = indexes.ToArray();
            if (arguments.Any(ParameterExpressionVisitor.Test))
                throw new NotSupportedException("Positional indexes depending on the query row are not supported.");
            if (arguments.Any(ContainsServerRuntime))
                throw new NotSupportedException("Server runtime expressions cannot be evaluated as indexes.");
            if (!ParameterExpressionVisitor.Test(source) && arguments.Any(ParameterExpressionVisitor.Test))
                throw new NotSupportedException("Captured collection indexes depending on the query row are not supported.");
        }

        private static bool ContainsServerRuntime(Expression node)
        {
            var visitor = new ServerRuntimeVisitor();
            visitor.Visit(node);
            return visitor.Found;
        }

        private static bool IsSafeRuntimeUnary(UnaryExpression node)
        {
            if (node.NodeType == ExpressionType.ArrayLength && node.Method == null) return true;
            if (node.NodeType == ExpressionType.Not && node.Type == typeof(bool)) return true;
            if (node.NodeType == ExpressionType.UnaryPlus && node.Method == null) return true;
            if (node.NodeType != ExpressionType.Convert && node.NodeType != ExpressionType.ConvertChecked &&
                node.NodeType != ExpressionType.TypeAs) return false;
            if (node.Method != null || node.Type == typeof(byte) || node.Type == typeof(sbyte) ||
                node.Type == typeof(short) || node.Type == typeof(ushort)) return false;
            if (node.Type.IsAssignableFrom(node.Operand.Type)) return true;
            var from = Nullable.GetUnderlyingType(node.Operand.Type) ?? node.Operand.Type;
            var to = Nullable.GetUnderlyingType(node.Type) ?? node.Type;
            if (from.IsEnum || to.IsEnum) return false;
            var target = Type.GetTypeCode(to);
            switch (Type.GetTypeCode(from))
            {
                case TypeCode.Int32: return target == TypeCode.Int64 || target == TypeCode.Single ||
                    target == TypeCode.Double || target == TypeCode.Decimal;
                case TypeCode.Int64: return target == TypeCode.Single || target == TypeCode.Double || target == TypeCode.Decimal;
                case TypeCode.Single: return target == TypeCode.Double;
                default: return false;
            }
        }

        private static void ValidateCapturedRuntimeBranches(Expression node)
        {
            if (!ContainsServerRuntime(node)) return;
            var visitor = new RuntimeBranchVisitor();
            visitor.Visit(node);
            if (visitor.Invalid)
                throw new NotSupportedException("Captured branches combined with server runtime expressions are not supported.");
        }

        private sealed class ServerRuntimeVisitor : ExpressionVisitor
        {
            internal bool Found { get; private set; }

            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression == null && node.Member.DeclaringType == typeof(DateTime) &&
                    (node.Member.Name == nameof(DateTime.Now) || node.Member.Name == nameof(DateTime.UtcNow) ||
                     node.Member.Name == nameof(DateTime.Today))) Found = true;
                return base.VisitMember(node);
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Method.DeclaringType == typeof(Guid) && node.Method.Name == nameof(Guid.NewGuid)) Found = true;
                return base.VisitMethodCall(node);
            }
        }

        private sealed class RuntimeBranchVisitor : ExpressionVisitor
        {
            private bool _runtimeSequence;
            internal bool Invalid { get; private set; }

            protected override Expression VisitConditional(ConditionalExpression node)
            {
                if ((ContainsServerRuntime(node) || _runtimeSequence) &&
                    (ContainsClosedElement(node.IfTrue) || ContainsClosedElement(node.IfFalse)))
                    Invalid = true;
                return base.VisitConditional(node);
            }

            protected override Expression VisitBinary(BinaryExpression node)
            {
                if ((node.NodeType == ExpressionType.Coalesce || node.NodeType == ExpressionType.AndAlso ||
                    node.NodeType == ExpressionType.OrElse) && (ContainsServerRuntime(node) || _runtimeSequence) &&
                    ContainsClosedElement(node.Right))
                    Invalid = true;
                return base.VisitBinary(node);
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                var previous = _runtimeSequence;
                if (ContainsServerRuntime(node) && (node.Method.DeclaringType == typeof(Enumerable) ||
                    (node.Object != null && Reflection.IsEnumerable(node.Object.Type)))) _runtimeSequence = true;
                try
                {
                    return base.VisitMethodCall(node);
                }
                finally
                {
                    _runtimeSequence = previous;
                }
            }

            private static bool ContainsClosedElement(Expression node)
            {
                var visitor = new ClosedElementVisitor();
                visitor.Visit(node);
                return visitor.Found;
            }
        }

        private sealed class ClosedElementVisitor : ExpressionVisitor
        {
            internal bool Found { get; private set; }

            protected override Expression VisitBinary(BinaryExpression node)
            {
                if (node.NodeType == ExpressionType.ArrayIndex && !ParameterExpressionVisitor.Test(node)) Found = true;
                return base.VisitBinary(node);
            }

            protected override Expression VisitIndex(IndexExpression node)
            {
                if (!ParameterExpressionVisitor.Test(node)) Found = true;
                return base.VisitIndex(node);
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                var sequenceMethod = node.Method.DeclaringType == typeof(Enumerable) ||
                    node.Method.DeclaringType == typeof(Queryable) ||
                    (node.Object != null && Reflection.IsEnumerable(node.Object.Type));
                if (!ParameterExpressionVisitor.Test(node) &&
                    ((node.Method.IsSpecialName && node.Method.Name == "get_Item") ||
                     (node.Method.DeclaringType?.IsArray == true && node.Method.Name == "Get") ||
                     (sequenceMethod && (node.Method.Name == "First" || node.Method.Name == "FirstOrDefault" ||
                     node.Method.Name == "Last" || node.Method.Name == "LastOrDefault" ||
                     node.Method.Name == "Single" || node.Method.Name == "SingleOrDefault" ||
                     node.Method.Name == "ElementAt" || node.Method.Name == "ElementAtOrDefault" ||
                     node.Method.Name == "Min" || node.Method.Name == "Max")))) Found = true;
                return base.VisitMethodCall(node);
            }
        }

        private static bool ContainsClosedElement(Expression node)
        {
            var visitor = new ClosedElementVisitor();
            visitor.Visit(node);
            return visitor.Found;
        }
    }
}
