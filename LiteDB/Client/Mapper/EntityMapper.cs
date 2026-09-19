using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Class to map entity class to BsonDocument
    /// </summary>
    public class EntityMapper
    {
        private readonly CancellationToken _initializationToken;

        /// <summary>
        /// Indicate which Type this entity mapper is
        /// </summary>
        public Type ForType { get; }

        /// <summary>
        /// List all type members that will be mapped to/from BsonDocument
        /// </summary>
        public List<MemberMapper> Members { get; } = new List<MemberMapper>();

        // Shared by fluent builders so repeated Ignore calls retain strict member validation.
        internal HashSet<string> IgnoredMembers { get; } = new HashSet<string>();

        /// <summary>
        /// Indicate which member is _id
        /// </summary>
        public MemberMapper Id => this.Members.SingleOrDefault(x => x.FieldName == "_id");

        /// <summary>
        /// Get/Set a custom ctor function to create new entity instance
        /// </summary>
        public CreateObject CreateInstance { get; set; }

        internal bool PopulateMembers { get; set; } = true;

        public EntityMapper(Type forType, CancellationToken initializationToken = default)
        {
            _initializationToken = initializationToken;
            this.ForType = forType;
            IsInitialized = !initializationToken.CanBeCanceled;
        }

        /// <summary>
        /// Resolve expression to get member mapped
        /// </summary>
        public MemberMapper GetMember(Expression expr)
        {
            if (this.ForType.IsInterface)
            {
                var body = expr is LambdaExpression lambda ? lambda.Body : expr;
                while (body is UnaryExpression unary && (unary.NodeType == ExpressionType.Convert ||
                    unary.NodeType == ExpressionType.ConvertChecked)) body = unary.Operand;
                if (body is MemberExpression member && member.Member.Name == expr.GetPath())
                    return this.FindMember(member.Member);
            }
            return this.Members.FirstOrDefault(x => x.MemberName == expr.GetPath());
        }

        internal MemberMapper FindMember(MemberInfo member)
        {
            if (!this.ForType.IsInterface) return this.Members.FirstOrDefault(x => x.MemberName == member.Name);
            return this.Members.FirstOrDefault(x => x.ReflectedMember == member) ??
                this.Members.FirstOrDefault(x => x.MemberName == member.Name &&
                    (x.ReflectedMember == null ||
                    x.ReflectedMember.DeclaringType.GetInterfaces().Contains(member.DeclaringType)));
        }

        internal volatile bool IsInitialized;
        internal bool UsesCustomIdSelection;

        public void WaitForInitialization()
        {
            if
            (
                _initializationToken == default
                || _initializationToken == CancellationToken.None
                || _initializationToken.IsCancellationRequested
            )
            {
                return;
            }

            if (!_initializationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
            {
                throw new LiteException(LiteException.ENTITY_INITIALIZATION_FAILED, "Initialization timeout");
            }
        }
    }
}
