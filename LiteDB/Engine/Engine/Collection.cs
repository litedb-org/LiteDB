using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Returns all collection inside datafile
        /// </summary>
        public IEnumerable<string> GetCollectionNames()
        {
            return _header.GetCollections().Select(x => x.Key);
        }

        /// <summary>
        /// Drop collection including all documents, indexes and extended pages (do not support transactions)
        /// </summary>
        public bool DropCollection(string name)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(name));

            // drop collection is possible only in exclusive transaction for this
            if (_locker.IsInTransaction) throw LiteException.AlreadyExistsTransaction();

            return this.AutoTransaction(transaction =>
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Write, name, false);

                // if collection do not exist, just exit
                if (snapshot.CollectionPage == null) return false;

                LOG($"drop collection `{name}`", "COMMAND");

                // call drop collection service
                snapshot.DropCollection(transaction.Safepoint);

                // remove sequence number (if exists)
                _sequences.TryRemove(name, out var dummy);

                return true;
            });
        }

        /// <summary>
        /// Rename a collection (do not support transactions)
        /// </summary>
        public bool RenameCollection(string collection, string newName)
        {
            if (collection.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(collection));
            if (newName.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(newName));

            // rename collection is possible only in exclusive transaction for this
            if (_locker.IsInTransaction) throw LiteException.AlreadyExistsTransaction();

            // check for collection name
            if (collection.Equals(newName, StringComparison.OrdinalIgnoreCase)) throw LiteException.InvalidCollectionName(newName, "New name must be different from current name");

            // checks if newName are compatible
            CollectionService.CheckName(newName, _header);

            // rename collection is possible only in exclusive transaction for this
            if (_locker.IsInTransaction) throw LiteException.AlreadyExistsTransaction();

            return this.AutoTransaction(transaction =>
            {
                var currentSnapshot = transaction.CreateSnapshot(LockMode.Write, collection, false);
                var newSnapshot = transaction.CreateSnapshot(LockMode.Write, newName, false);

                if (currentSnapshot.CollectionPage == null) return false;

                // checks if do not already exists this collection name
                if (_header.GetCollectionPageID(newName) != uint.MaxValue)
                {
                    throw LiteException.AlreadyExistsCollectionName(newName);
                }

                // rename collection and set page as dirty (there is no need to set IsDirty in HeaderPage)
                transaction.Pages.Commit += (h) =>
                {
                    h.RenameCollection(collection, newName);
                };

                return true;
            });
        }
    }
}