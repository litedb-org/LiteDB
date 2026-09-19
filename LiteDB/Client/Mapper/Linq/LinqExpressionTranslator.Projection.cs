using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using static LiteDB.BsonExpressionFactory;

namespace LiteDB
{
    internal sealed partial class LinqExpressionTranslator
    {
        private BsonExpression TranslateNew(NewExpression node)
        {
            if (node.Members != null)
            {
                return Document(node.Members.Select((member, i) =>
                    new KeyValuePair<string, BsonExpression>(member.Name, Translate(node.Arguments[i]))), _parameters);
            }
            if (TryGetResolver(node.Type, out var resolver))
            {
                var binding = resolver.ResolveCtor(node.Constructor);
                if (binding != null) return binding(new LinqBindingContext(this, null, node.Arguments));
            }
            throw Unsupported(node, node.Type.Name + " constructor");
        }

        private BsonExpression TranslateInitializer(MemberInitExpression node)
        {
            if (node.NewExpression.Constructor.GetParameters().Length != 0)
                throw Unsupported(node, "constructor with parameters; use property initializers");
            var members = new List<KeyValuePair<string, BsonExpression>>();
            foreach (var binding in node.Bindings)
            {
                if (!(binding is MemberAssignment assignment)) throw Unsupported(node, binding.Member.Name);
                var name = ResolveMember(assignment.Member, node.Type, out var member);
                var value = TryDbRef(assignment.Expression, member, false) ?? Translate(assignment.Expression);
                members.Add(new KeyValuePair<string, BsonExpression>(name, value));
            }
            return Document(members, _parameters);
        }

        private BsonExpression TryDbRef(Expression node, MemberMapper member, bool inList)
        {
            if (!member.IsDbRef) return null;
            if (string.IsNullOrWhiteSpace(member.DbRefCollectionName))
                throw new NotSupportedException($"BsonRefId<T> requires a DbRef collection name. Member '{member.MemberName}' is missing it (use [BsonRef] or Entity<T>().DbRef(...)).");
            if (TrySerializeCapturedDbRef(node, member, inList, out var serialized))
                return Bind(serialized ?? BsonValue.Null);
            if (!inList && node is ConditionalExpression conditional)
                return Call("IIF", new[] { Translate(conditional.Test),
                    TryDbRef(conditional.IfTrue, member, false) ?? Translate(conditional.IfTrue),
                    TryDbRef(conditional.IfFalse, member, false) ?? Translate(conditional.IfFalse) }, _context, _parameters);
            if (!inList && node is BinaryExpression coalesce && coalesce.NodeType == ExpressionType.Coalesce && coalesce.Conversion == null)
                return Call("COALESCE", new[] { TryDbRef(coalesce.Left, member, false) ?? Translate(coalesce.Left),
                    TryDbRef(coalesce.Right, member, false) ?? Translate(coalesce.Right) }, _context, _parameters);
            switch (node)
            {
                case UnaryExpression { NodeType: ExpressionType.Convert, Method: { Name: "op_Implicit" } } conversion:
                    return TryDbRef(conversion.Operand, member, inList);
                case NewExpression creation when creation.Members == null && creation.Type.IsConstructedGenericType &&
                    creation.Type.GetGenericTypeDefinition() == typeof(BsonRefId<>):
                    var refType = creation.Type.GetGenericArguments()[0];
                    if (!member.DataType.IsAssignableFrom(refType) && !(inList && member.UnderlyingType.IsAssignableFrom(refType))) return null;
                    var fields = new List<KeyValuePair<string, BsonExpression>>
                    {
                        new KeyValuePair<string, BsonExpression>("$id", Translate(creation.Arguments[0])),
                        new KeyValuePair<string, BsonExpression>("$ref", Bind(member.DbRefCollectionName))
                    };
                    if (refType != member.UnderlyingType)
                        fields.Add(new KeyValuePair<string, BsonExpression>("$type", Bind(_mapper.SerializeTypeName(refType))));
                    return Document(fields, _parameters);
                case NewArrayExpression array when !inList && array.Type.IsArray && member.UnderlyingType.IsAssignableFrom(array.Type.GetElementType()):
                    return Array(TranslateDbRefItems(array.Expressions, member), _parameters);
                case ListInitExpression list when !inList && list.Type.IsConstructedGenericType &&
                    list.Type.GetGenericTypeDefinition() == typeof(List<>) && member.UnderlyingType.IsAssignableFrom(list.Type.GetGenericArguments()[0]):
                    return Array(TranslateDbRefItems(list.Initializers.Select(x => x.Arguments.Count == 1 && x.AddMethod.Name == "Add" ?
                        x.Arguments[0] : throw Unsupported(node, x.AddMethod.Name)), member), _parameters);
                default: return null;
            }
        }

        private IEnumerable<BsonExpression> TranslateDbRefItems(IEnumerable<Expression> items, MemberMapper member)
        {
            foreach (var item in items)
            {
                if (TrySerializeCapturedDbRef(item, member, true, out var serialized))
                {
                    if (serialized != null) yield return Bind(serialized);
                    continue;
                }
                yield return TryDbRef(item, member, true) ?? throw Unsupported(item, "BsonRefId<T>");
            }
        }

        private bool TrySerializeCapturedDbRef(Expression node, MemberMapper member, bool inList, out BsonValue serialized)
        {
            serialized = null;
            if (ParameterExpressionVisitor.Test(node) || ContainsServerRuntime(node)) return false;
            var markers = new DbRefMarkerVisitor();
            markers.Visit(node);
            if (markers.Found) return false;
            var value = Evaluate(node);
            serialized = inList ? member.Serialize(new[] { value }, _mapper).AsArray.FirstOrDefault() :
                member.Serialize(value, _mapper);
            return true;
        }

        private sealed class DbRefMarkerVisitor : ExpressionVisitor
        {
            internal bool Found { get; private set; }
            public override Expression Visit(Expression node)
            {
                if (node != null && node.Type.IsGenericType &&
                    node.Type.GetGenericTypeDefinition() == typeof(BsonRefId<>)) Found = true;
                return base.Visit(node);
            }
        }
    }
}
