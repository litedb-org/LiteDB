using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal class CollectionService
    {
        private readonly HeaderPage _header;
        private readonly DiskService _disk;
        private readonly Snapshot _snapshot;
        private readonly TransactionPages _transPages;

        public CollectionService(HeaderPage header, DiskService disk, Snapshot snapshot, TransactionPages transPages)
        {
            _header = header;
            _disk = disk;
            _snapshot = snapshot;
            _transPages = transPages;
        }

        /// <summary>
        /// Check collection name if is valid (and fit on header)
        /// Throw correct message error if not valid name or not fit on header page
        /// </summary>
        public static void CheckName(string name, HeaderPage header)
        {
            if (Encoding.UTF8.GetByteCount(name) > header.GetAvailableCollectionSpace()) throw LiteException.InvalidCollectionName(name, "There is no space in header this collection name");
            if (!name.IsWord()) throw LiteException.InvalidCollectionName(name, "Use only [a-Z$_]");
            if (name.StartsWith("$")) throw LiteException.InvalidCollectionName(name, "Collection can't starts with `$` (reserved for system collections)");
        }

        /// <summary>
        /// Get collection page instance (or create a new one). Returns true if a new collection was created
        /// </summary>
        public bool Get(string name, bool addIfNotExists, ref CollectionPage collectionPage)
        {
            // get collection pageID from header
            var pageID = _header.GetCollectionPageID(name);

            if (pageID != uint.MaxValue)
            {
                collectionPage = _snapshot.GetPage<CollectionPage>(pageID);

                return false;
            }
            else if (addIfNotExists)
            {
                this.Add(name, ref collectionPage);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Add a new collection. Check if name the not exists. Create only in transaction page - will update header only in commit
        /// </summary>
        private void Add(string name, ref CollectionPage collectionPage)
        {
            // checks for collection name/size
            CheckName(name, _header);

            // create new collection page
            collectionPage = _snapshot.NewPage<CollectionPage>();
            var pageID = collectionPage.PageID;

            // insert collection name/pageID in header only in commit operation
            _transPages.Commit += (h) => h.InsertCollection(name, pageID);

            // create first index (_id pk) (must pass collectionPage because snapshot contains null in CollectionPage prop)
            var indexer = new IndexService(_snapshot, _header.Pragmas.Collation, _disk.MAX_ITEMS_COUNT);

            indexer.CreateIndex("_id", "$._id", true);
        }
    }
}