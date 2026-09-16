using System;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    internal partial class LinqExpressionVisitor
    {
        private bool TryVisitCapturedElement(Expression node)
        {
            var indexedSource = node is BinaryExpression binary ? binary.Left :
                node is IndexExpression index ? index.Object :
                node is MethodCallExpression method && IsIndexAccess(method) ? method.Object ?? method.Arguments[0] : null;
            var runtime = ContainsServerRuntime(node);
            if (indexedSource != null && HasRowDependentIndex(node))
                throw new NotSupportedException("Positional indexes depending on the query row are not supported.");
            if (indexedSource != null && runtime)
                throw new NotSupportedException("Server runtime expressions cannot be evaluated as captured indexes.");
            if (ParameterExpressionVisitor.Test(node))
            {
                if (indexedSource != null && !ParameterExpressionVisitor.Test(indexedSource))
                    throw new NotSupportedException("Captured collection indexes depending on the query row are not supported.");
                return false;
            }
            if (runtime)
            {
                var branches = new ElementAccessVisitor();
                branches.Visit(node);
                if (branches.HasConditional)
                    throw new NotSupportedException("Captured branches containing server runtime expressions are not supported.");
                return false;
            }
            this.VisitConstant(Expression.Constant(this.Evaluate(node)));
            return true;
        }

        private static bool HasRowDependentIndex(Expression node)
        {
            if (node is BinaryExpression binary) return ParameterExpressionVisitor.Test(binary.Right);
            if (node is IndexExpression index) return index.Arguments.Any(ParameterExpressionVisitor.Test);
            var method = (MethodCallExpression)node;
            return method.Arguments.Skip(method.Object == null ? 1 : 0).Any(ParameterExpressionVisitor.Test);
        }

        private static bool IsIndexAccess(MethodCallExpression method)
        {
            return (method.Method.IsSpecialName && method.Method.Name == "get_Item") ||
                (method.Method.DeclaringType?.IsArray == true && method.Method.Name == "Get") ||
                (IsEnumerableAccess(method) && (method.Method.Name == "ElementAt" || method.Method.Name == "ElementAtOrDefault"));
        }

        private static bool IsEnumerableAccess(MethodCallExpression method)
        {
            return method.Method.DeclaringType == typeof(Enumerable) ||
                (method.Object != null && (Reflection.IsCollection(method.Object.Type) || Reflection.IsEnumerable(method.Object.Type)));
        }

        private static bool IsElementAccess(MethodCallExpression method)
        {
            if (IsIndexAccess(method)) return true;
            if (IsEnumerableAccess(method))
            {
                switch (method.Method.Name)
                {
                    case "First":
                    case "FirstOrDefault":
                    case "Last":
                    case "LastOrDefault":
                    case "Single":
                    case "SingleOrDefault":
                    case "ElementAt":
                    case "ElementAtOrDefault":
                    case "Min":
                    case "Max":
                        return true;
                }
            }
            return false;
        }

        private bool TryVisitCapturedElementMember(MemberExpression node)
        {
            var visitor = new ElementAccessVisitor();
            visitor.Visit(node);
            if (!visitor.Found) return false;
            if (ContainsServerRuntime(node))
            {
                // Scalar resolver members can still wrap a server-side sequence. Plain
                // CLR member getters cannot be applied to that sequence's captured source.
                if (visitor.HasConditional || !TryGetResolver(node.Member.DeclaringType, out _))
                    throw new NotSupportedException("Captured element members containing server runtime expressions are not supported.");
                return false;
            }
            this.VisitConstant(Expression.Constant(this.Evaluate(node)));
            return true;
        }

        private bool TryVisitCapturedBranch(Expression node)
        {
            if (ParameterExpressionVisitor.Test(node) || ContainsServerRuntime(node)) return false;
            var visitor = new ElementAccessVisitor();
            visitor.Visit(node);
            if (!visitor.Found) return false;
            this.VisitConstant(Expression.Constant(this.Evaluate(node)));
            return true;
        }

        private bool TryVisitCapturedUnary(UnaryExpression node)
        {
            if ((node.NodeType != ExpressionType.Convert && node.NodeType != ExpressionType.ConvertChecked &&
                 node.NodeType != ExpressionType.TypeAs && node.NodeType != ExpressionType.Negate &&
                 node.NodeType != ExpressionType.NegateChecked && node.NodeType != ExpressionType.UnaryPlus &&
                 node.NodeType != ExpressionType.Not && node.NodeType != ExpressionType.ArrayLength) ||
                ParameterExpressionVisitor.Test(node)) return false;
            var visitor = new ElementAccessVisitor();
            visitor.Visit(node);
            if (!visitor.Found) return false;
            if (ContainsServerRuntime(node))
            {
                var safeOperator = node.Method == null && (node.NodeType == ExpressionType.ArrayLength ||
                    node.NodeType == ExpressionType.UnaryPlus || (node.NodeType == ExpressionType.Not && node.Type == typeof(bool)));
                if (visitor.HasConditional || (!IsSafeRuntimeConversion(node) && !safeOperator))
                    throw new NotSupportedException("Captured unary expressions containing unsupported server runtime combinations are not supported.");
                return false;
            }
            this.VisitConstant(Expression.Constant(this.Evaluate(node)));
            return true;
        }

        private static bool IsSafeRuntimeConversion(UnaryExpression node)
        {
            if (node.Method != null || (node.NodeType != ExpressionType.Convert &&
                node.NodeType != ExpressionType.ConvertChecked && node.NodeType != ExpressionType.TypeAs)) return false;
            if (node.Type.IsAssignableFrom(node.Operand.Type)) return true;
            if (node.NodeType == ExpressionType.TypeAs) return false;
            var fromNullable = Nullable.GetUnderlyingType(node.Operand.Type);
            var toNullable = Nullable.GetUnderlyingType(node.Type);
            if (fromNullable != null && toNullable == null) return false;
            var from = fromNullable ?? node.Operand.Type;
            var to = toNullable ?? node.Type;
            if (from == to) return true;
            if (from.IsEnum || to.IsEnum) return false;
            var target = Type.GetTypeCode(to);
            // Preserve established server paths for implicit widening between numeric
            // BSON representations. Char is stored as a string and is not numeric here.
            return Type.GetTypeCode(from) switch
            {
                TypeCode.SByte => target is TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.Byte => target is TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.Int16 => target is TypeCode.Int32 or TypeCode.Int64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.UInt16 => target is TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.Int32 => target is TypeCode.Int64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.UInt32 => target is TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.Int64 or TypeCode.UInt64 => target is TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
                TypeCode.Single => target == TypeCode.Double,
                _ => false
            };
        }

        private sealed class ElementAccessVisitor : ExpressionVisitor
        {
            private readonly bool _inspectBranches;
            private bool _runtimeSequence;
            public bool Found { get; private set; }
            public bool FoundClosed { get; private set; }
            public bool HasConditional { get; private set; }
            public bool HasRuntimeConditional { get; private set; }

            public ElementAccessVisitor(bool inspectBranches = true) => _inspectBranches = inspectBranches;

            private static bool HasClosedElement(Expression node)
            {
                var visitor = new ElementAccessVisitor(false);
                visitor.Visit(node);
                return visitor.FoundClosed;
            }
            private void CheckBranch(Expression node, bool hasCapturedBranch)
            {
                if (!_inspectBranches || !hasCapturedBranch) return;
                var runtime = ContainsServerRuntime(node);
                var dependent = ParameterExpressionVisitor.Test(node);
                // Closed, non-runtime branches are evaluated atomically by the capture
                // binder. A lambda over a runtime sequence still needs deferred branches.
                HasConditional |= runtime || dependent;
                HasRuntimeConditional |= runtime || (_runtimeSequence && dependent);
            }

            protected override Expression VisitConditional(ConditionalExpression node)
            {
                if (_inspectBranches) this.CheckBranch(node, HasClosedElement(node.IfTrue) || HasClosedElement(node.IfFalse));
                return base.VisitConditional(node);
            }
            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (IsElementAccess(node))
                {
                    Found = true;
                    if (!ParameterExpressionVisitor.Test(node)) FoundClosed = true;
                }
                var previous = _runtimeSequence;
                if (_inspectBranches && IsEnumerableAccess(node) && ContainsServerRuntime(node)) _runtimeSequence = true;
                try
                {
                    return base.VisitMethodCall(node);
                }
                finally
                {
                    _runtimeSequence = previous;
                }
            }
            protected override Expression VisitBinary(BinaryExpression node)
            {
                if (node.NodeType == ExpressionType.ArrayIndex)
                {
                    Found = true;
                    if (!ParameterExpressionVisitor.Test(node)) FoundClosed = true;
                }
                if (_inspectBranches && (node.NodeType == ExpressionType.Coalesce || node.NodeType == ExpressionType.AndAlso ||
                    node.NodeType == ExpressionType.OrElse)) this.CheckBranch(node, HasClosedElement(node.Right));
                return base.VisitBinary(node);
            }
            protected override Expression VisitIndex(IndexExpression node)
            {
                Found = true;
                if (!ParameterExpressionVisitor.Test(node)) FoundClosed = true;
                return base.VisitIndex(node);
            }
        }

        private static bool ContainsElementAccess(Expression node)
        {
            var visitor = new ElementAccessVisitor(false);
            visitor.Visit(node);
            return visitor.Found;
        }

        private static void ValidateCapturedRuntimeBranches(Expression node)
        {
            if (!ContainsServerRuntime(node)) return;
            var visitor = new ElementAccessVisitor();
            visitor.Visit(node);
            if (visitor.HasRuntimeConditional)
                throw new NotSupportedException("Captured short-circuit branches combined with server runtime expressions are not supported.");
        }

        private static bool ContainsServerRuntime(Expression node)
        {
            var visitor = new ServerRuntimeVisitor();
            visitor.Visit(node);
            return visitor.Found;
        }

        // Preserve volatile server clocks and ID generators. Deterministic resolver
        // constants (Guid.Empty, ObjectId.Empty, string.Empty) remain CLR captures.
        private sealed class ServerRuntimeVisitor : ExpressionVisitor
        {
            public bool Found { get; private set; }
            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression == null && node.Member.DeclaringType == typeof(DateTime) &&
                    (node.Member.Name == "Now" || node.Member.Name == "UtcNow" || node.Member.Name == "Today")) Found = true;
                return base.VisitMember(node);
            }
            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Method.DeclaringType == typeof(Guid) && node.Method.Name == "NewGuid") Found = true;
                return base.VisitMethodCall(node);
            }
        }

        protected override Expression VisitIndex(IndexExpression node)
        {
            if (this.TryVisitCapturedElement(node)) return node;
            if (node.Indexer != null)
            {
                this.Visit(Expression.Call(node.Object, node.Indexer.GetGetMethod(), node.Arguments));
                return node;
            }
            if (node.Arguments.Count == 1)
            {
                this.Visit(Expression.ArrayIndex(node.Object, node.Arguments[0]));
                return node;
            }
            throw new NotSupportedException("Multidimensional query arrays are not supported.");
        }
    }
}
