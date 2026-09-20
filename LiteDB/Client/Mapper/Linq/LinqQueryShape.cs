using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Reflection;

namespace LiteDB
{
    // Structural keys contain CLR metadata, never closure objects or constant values.
    // Expressions are retained only during this translation/binding invocation.
    internal sealed class LinqQueryShape : ExpressionVisitor
    {
        [ThreadStatic]
        private static LinqQueryShape _available;
        internal readonly List<Expression> Expressions = new List<Expression>();
        internal readonly List<Token> Tokens = new List<Token>();
        private readonly Dictionary<ParameterExpression, int> _parameters = new Dictionary<ParameterExpression, int>();
        internal bool Supported { get; private set; } = true;
        internal int Hash { get; private set; } = 17;

        private LinqQueryShape() { }

        internal static LinqQueryShape Rent()
        {
            var shape = _available ?? new LinqQueryShape();
            _available = null; // A captured getter can reenter translation on this thread.
            return shape;
        }

        internal void Release()
        {
            // Clear all references before pooling: closures and expression trees must
            // not survive a call merely because its thread stays alive.
            Expressions.Clear();
            Tokens.Clear();
            _parameters.Clear();
            Supported = true;
            Hash = 17;
            _available = this;
        }

        public override Expression Visit(Expression node)
        {
            if (!Supported) return node;
            if (Tokens.Count >= 512) { Supported = false; return node; }
            var token = new Token { Kind = node?.NodeType ?? default, Type = node?.Type };
            switch (node)
            {
                case null: break;
                case ConstantExpression _: break;
                case ParameterExpression parameter:
                    if (!_parameters.TryGetValue(parameter, out var ordinal))
                    {
                        ordinal = _parameters.Count;
                        _parameters.Add(parameter, ordinal);
                    }
                    token.Flags = ordinal;
                    break;
                case MemberExpression member: token.Member = member.Member; break;
                case MethodCallExpression call:
                    // Indexer arguments become part of Source, rather than parameter slots.
                    if (call.Method.Name == "get_Item") Supported = false;
                    token.Member = call.Method;
                    break;
                case BinaryExpression binary:
                    if (binary.NodeType == ExpressionType.ArrayIndex) Supported = false;
                    token.Member = binary.Method;
                    token.Flags = (binary.IsLifted ? 1 : 0) | (binary.IsLiftedToNull ? 2 : 0);
                    break;
                case UnaryExpression unary:
                    token.Member = unary.Method;
                    token.Flags = (unary.IsLifted ? 1 : 0) | (unary.IsLiftedToNull ? 2 : 0);
                    break;
                case NewExpression creation:
                    token.Member = creation.Constructor;
                    token.Members = creation.Members;
                    break;
                case LambdaExpression _:
                case ConditionalExpression _: break;
                // Variable-arity nodes need child counts: a preorder token stream
                // alone cannot distinguish a nested child from its next sibling.
                case MemberInitExpression initializer: token.Flags = initializer.Bindings.Count; break;
                case NewArrayExpression array: token.Flags = array.Expressions.Count; break;
                default: Supported = false; break;
            }
            Add(token, node);
            return Supported ? base.Visit(node) : node;
        }

        protected override MemberAssignment VisitMemberAssignment(MemberAssignment node)
        {
            Add(new Token { Kind = ExpressionType.MemberInit, Member = node.Member }, null);
            return base.VisitMemberAssignment(node);
        }

        protected override MemberMemberBinding VisitMemberMemberBinding(MemberMemberBinding node)
        {
            Supported = false;
            return node;
        }

        protected override MemberListBinding VisitMemberListBinding(MemberListBinding node)
        {
            Supported = false;
            return node;
        }

        private void Add(Token token, Expression node)
        {
            Tokens.Add(token);
            Expressions.Add(node);
            unchecked { Hash = Hash * 31 + token.GetHashCode(); }
        }

        internal struct Token : IEquatable<Token>
        {
            internal ExpressionType Kind;
            internal Type Type;
            internal MemberInfo Member;
            internal ReadOnlyCollection<MemberInfo> Members;
            internal int Flags;

            public bool Equals(Token other)
            {
                if (Kind != other.Kind || Type != other.Type || !Equals(Member, other.Member) || Flags != other.Flags ||
                    (Members?.Count ?? -1) != (other.Members?.Count ?? -1)) return false;
                if (Members != null)
                    for (var i = 0; i < Members.Count; i++)
                        if (!Equals(Members[i], other.Members[i])) return false;
                return true;
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = (((int)Kind * 31 + (Type?.GetHashCode() ?? 0)) * 31 + (Member?.GetHashCode() ?? 0)) * 31 + Flags;
                    if (Members != null) foreach (var member in Members) hash = hash * 31 + member.GetHashCode();
                    return hash;
                }
            }
        }
    }
}
