using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    internal partial class LinqExpressionVisitor
    {
        private bool TryVisitCapturedDbRef(Expression node, MemberMapper member, bool isInList)
        {
            if (this.TrySerializeCapturedDbRef(node, member, isInList, out var serialized))
            {
                this.VisitConstant(Expression.Constant(serialized ?? BsonValue.Null));
                return true;
            }
            if (!isInList && node is ConditionalExpression conditional)
            {
                _builder.Append("IIF(");
                this.Visit(conditional.Test);
                _builder.Append(", ");
                this.VisitDbRefBranch(conditional.IfTrue, member);
                _builder.Append(", ");
                this.VisitDbRefBranch(conditional.IfFalse, member);
                _builder.Append(")");
                return true;
            }
            if (!isInList && node is BinaryExpression coalesce && node.NodeType == ExpressionType.Coalesce && coalesce.Conversion == null)
            {
                _builder.Append("COALESCE(");
                this.VisitDbRefBranch(coalesce.Left, member);
                _builder.Append(", ");
                this.VisitDbRefBranch(coalesce.Right, member);
                _builder.Append(")");
                return true;
            }
            if (node is UnaryExpression conversion && conversion.Method == null &&
                conversion.NodeType == ExpressionType.Convert && node.Type.IsAssignableFrom(conversion.Operand.Type))
                return this.TryVisitDbRefIdExpression(conversion.Operand, member, isInList);
            return false;
        }

        private void VisitDbRefBranch(Expression node, MemberMapper member)
        {
            if (!this.TryVisitDbRefIdExpression(node, member)) this.Visit(node);
        }

        private bool TrySerializeCapturedDbRef(Expression node, MemberMapper member, bool isInList, out BsonValue serialized)
        {
            serialized = null;
            if (ParameterExpressionVisitor.Test(node) || ContainsServerRuntime(node)) return false;
            var markers = new DbRefMarkerVisitor();
            markers.Visit(node);
            if (markers.Found) return false;

            // The destination member owns DbRef serialization, including collection
            // names, polymorphic type tags and lists. Serializing the entity type
            // alone would persist a full inline document instead of a reference.
            var value = this.Evaluate(node);
            serialized = isInList
                ? member.Serialize(new[] { value }, _mapper).AsArray.FirstOrDefault()
                : member.Serialize(value, _mapper);
            return true;
        }

        private void VisitDbRefList(IEnumerable<Expression> items, MemberMapper member)
        {
            _builder.Append("[ ");
            var hasItem = false;
            foreach (var item in items)
            {
                var captured = this.TrySerializeCapturedDbRef(item, member, true, out var serialized);
                if (captured && serialized == null) continue; // The destination list serializer omitted this item.
                if (hasItem) _builder.Append(", ");
                if (captured) this.VisitConstant(Expression.Constant(serialized));
                else if (!this.TryVisitDbRefIdExpression(item, member, true))
                    throw new NotSupportedException($"Expression {item} not supported for DbRef list assignment.");
                hasItem = true;
            }
            _builder.Append(" ]");
        }

        private sealed class DbRefMarkerVisitor : ExpressionVisitor
        {
            public bool Found { get; private set; }

            public override Expression Visit(Expression node)
            {
                // BsonRefId is an expression-only marker whose conversion throws
                // in CLR. Leave its existing server-side translation in charge.
                if (node != null && node.Type.IsGenericType &&
                    node.Type.GetGenericTypeDefinition() == typeof(BsonRefId<>)) Found = true;
                return base.Visit(node);
            }
        }
    }
}
