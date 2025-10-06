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

        /// <summary>
        /// Indicate which member is _id
        /// </summary>
        public MemberMapper Id => this.Members.SingleOrDefault(x => x.FieldName == "_id");

        /// <summary>
        /// Get/Set a custom ctor function to create new entity instance
        /// </summary>
        public CreateObject CreateInstance { get; set; }

        public EntityMapper(Type forType, CancellationToken initializationToken = default)
        {
            _initializationToken = initializationToken;
            this.ForType = forType;
        }

        /// <summary>
        /// Resolve expression to get member mapped
        /// </summary>
        public MemberMapper GetMember(Expression expr)
        {
            return this.Members.FirstOrDefault(x => x.MemberName == expr.GetPath());
        }

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