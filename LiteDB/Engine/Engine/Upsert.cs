using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Implement upsert command to documents in a collection. Calls update on all documents,
        /// then any documents not updated are then attempted to insert.
        /// This will have the side effect of throwing if duplicate items are attempted to be inserted.
        /// </summary>
        public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        {
            if (collection.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(collection));
            if (docs == null) throw new ArgumentNullException(nameof(docs));

            return this.AutoTransaction(transaction =>
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Write, collection, true);
                var collectionPage = snapshot.CollectionPage;
                var indexer = new IndexService(snapshot, _header.Pragmas.Collation, _disk.MAX_ITEMS_COUNT);
                var data = new DataService(snapshot, _disk.MAX_ITEMS_COUNT);
                var count = 0;

                LOG($"upsert `{collection}`", "COMMAND");

                foreach (var doc in docs)
                {
                    _state.Validate();

                    transaction.Safepoint();

                    // first try update document (if exists _id), if not found, do insert
                    if (doc["_id"] == BsonValue.Null || this.UpdateDocument(snapshot, collectionPage, doc, indexer, data) == false)
                    {
                        this.InsertDocument(snapshot, doc, autoId, indexer, data);
                        count++;
                    }
                }
                
                // returns how many document was inserted
                return count;
            });
        }
    }
}