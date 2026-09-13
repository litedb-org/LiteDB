using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        /// <summary>
        /// Insert a new entity to this collection. Document Id must be a new value in collection - Returns document Id
        /// </summary>
        public BsonValue Insert(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            var doc = _mapper.ToDocument(entity);
            var removed = this.RemoveDocId(doc);

            _engine.Insert(_collection, new[] { doc }, _autoId);

            var id = doc["_id"];

            // checks if must update _id value in entity
            if (removed)
            {
                _id.Setter(entity, id.RawValue);
            }

            return id;
        }

        /// <summary>
        /// Insert a new document to this collection using passed id value.
        /// </summary>
        public void Insert(BsonValue id, T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));

            var doc = _mapper.ToDocument(entity);

            doc["_id"] = id;

            _engine.Insert(_collection, new [] { doc }, _autoId);
        }

        /// <summary>
        /// Insert an array of new documents to this collection. Document Id must be a new value in collection. Can be set buffer size to commit at each N documents
        /// </summary>
        public int Insert(IEnumerable<T> entities)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            return _engine.Insert(_collection, this.GetBsonDocs(entities), _autoId);
        }

        /// <summary>
        /// Implements bulk insert documents in a collection. Usefull when need lots of documents.
        /// </summary>
        [Obsolete("Use normal Insert()")]
        public int InsertBulk(IEnumerable<T> entities, int batchSize = 5000)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            return _engine.Insert(_collection, this.GetBsonDocs(entities), _autoId);
        }

        /// <summary>
        /// Convert each T document in a BsonDocument, setting autoId for each one
        /// </summary>
        private IEnumerable<BsonDocument> GetBsonDocs(IEnumerable<T> documents)
        {
            foreach (var document in documents)
            {
                var doc = _mapper.ToDocument(document);
                var removed = this.RemoveDocId(doc);

                yield return doc;

                if (removed && _id != null)
                {
                    _id.Setter(document, doc["_id"].RawValue);
                }
            }
        }

        /// <summary>
        /// Remove document _id if contains a "empty" value (checks for autoId bson type)
        /// </summary>
        private bool RemoveDocId(BsonDocument doc)
        {
            if (_id != null && doc.TryGetValue("_id", out var id)) 
            {
                // check if exists _autoId and current id is "empty"
                if ((_autoId == BsonAutoId.Int32 && (id.IsInt32 && id.AsInt32 == 0)) ||
                    (_autoId == BsonAutoId.ObjectId && (id.IsNull || (id.IsObjectId && id.AsObjectId == ObjectId.Empty))) ||
                    (_autoId == BsonAutoId.Guid && id.IsGuid && id.AsGuid == Guid.Empty) ||
                    (_autoId == BsonAutoId.Int64 && id.IsInt64 && id.AsInt64 == 0))
                {
                    // in this cases, remove _id and set new value after
                    doc.Remove("_id");
                    return true;
                }
            }

            return false;   
        }
    }
}