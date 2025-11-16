using LiteDB.Engine;
using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <inheritdoc cref="ILiteCollection{T}"/>
    public sealed partial class LiteCollection<T> : ILiteCollection<T>
    {
        private readonly string _collection;
        private readonly ILiteEngine _engine;
        private readonly List<BsonExpression> _includes;
        private readonly BsonMapper _mapper;
        private readonly EntityMapper _entity;
        private readonly MemberMapper _id;
        private readonly BsonAutoId _autoId;

        /// <inheritdoc/>
        public string Name => _collection;

        /// <inheritdoc/>
        public BsonAutoId AutoId => _autoId;

        /// <inheritdoc/>
        public EntityMapper EntityMapper => _entity;

        /// <summary>
        /// Initializes a new instance of the LiteCollection class for the specified collection name, auto ID strategy,
        /// database engine, and BSON mapper.
        /// </summary>
        /// <remarks>If the collection is strongly typed, the constructor determines the appropriate auto
        /// ID strategy based on the entity's ID member type. For untyped collections, the provided auto ID strategy is
        /// used directly.</remarks>
        /// <param name="name">The name of the collection to operate on. If null, the collection name is resolved from the type using the
        /// provided mapper.</param>
        /// <param name="autoId">The auto ID generation strategy to use for documents in the collection.</param>
        /// <param name="engine">The database engine instance used to perform operations on the collection.</param>
        /// <param name="mapper">The BSON mapper used for object-document mapping and collection name resolution.</param>
        internal LiteCollection(string name, BsonAutoId autoId, ILiteEngine engine, BsonMapper mapper)
        {
            _collection = name ?? mapper.ResolveCollectionName(typeof(T));
            _engine = engine;
            _mapper = mapper;
            _includes = new List<BsonExpression>();

            // if strong typed collection, get _id member mapped (if exists)
            if (typeof(T) == typeof(BsonDocument))
            {
                _entity = null;
                _id = null;
                _autoId = autoId;
            }
            else
            {
                _entity = mapper.GetEntityMapper(typeof(T));
                _entity.WaitForInitialization();
                
                _id = _entity.Id;

                if (_id != null && _id.AutoId)
                {
                    _autoId =
                        _id.DataType == typeof(Int32) || _id.DataType == typeof(Int32?) ? BsonAutoId.Int32 :
                        _id.DataType == typeof(Int64) || _id.DataType == typeof(Int64?) ? BsonAutoId.Int64 :
                        _id.DataType == typeof(Guid) || _id.DataType == typeof(Guid?) ? BsonAutoId.Guid :
                        BsonAutoId.ObjectId;
                }
                else
                {
                    _autoId = autoId;
                }
            }
        }
    }
}