using System;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        private static LiteException CollationMismatch() => new LiteException(0,
            "Database index ordering/collation differs from this comparer or runtime. Export records in the " +
            "original compatible environment and import here. For culture-only changes, an Ordinal rebuild " +
            "in the original environment can also prepare the file.");

        private void ValidateCollationStamp()
        {
            if (_header.Pragmas.IndexOrderVersion > EnginePragmas.INDEX_ORDER_VERSION)
                throw CollationMismatch();
            var stored = _header.Pragmas.CollationStamp;
            if (stored != 0 && stored != CollationFingerprint.Compute(_header.Pragmas.Collation))
                throw CollationMismatch();
        }

        private void ValidateLegacyCollation(bool migrating = false)
        {
            if (!migrating && _header.Pragmas.CollationStamp != 0) return;
            // Legacy files have no runtime signature. Validate their actual level-zero
            // ordering before admitting any query or write, without modifying the file.
            var transaction = _monitor.GetTransaction(true, true, out _);
            try
            {
                var incompatible = false;
                foreach (var collection in _header.GetCollections())
                {
                    var snapshot = transaction.CreateSnapshot(LockMode.Read, collection.Key, false);
                    var indexes = new IndexService(snapshot, _header.Pragmas.Collation, _disk.MAX_ITEMS_COUNT);
                    foreach (var index in snapshot.CollectionPage.GetCollectionIndexes())
                    {
                        if (index.IndexType != 0) continue;
                        BsonValue previous = null;
                        foreach (var node in indexes.FindAll(index, LiteDB.Query.Ascending))
                        {
                            var key = node.Key;
                            if (previous != null)
                            {
                                var order = previous.CompareTo(key, _header.Pragmas.Collation);
                                if (order > 0 || (order == 0 && index.Unique)) incompatible = true;
                            }
                            previous = key;
                            transaction.Safepoint();
                        }
                    }
                }
                // Inspect every index first: later structural corruption must keep
                // its corruption diagnostic and explicitly requested recovery path.
                if (incompatible && !migrating) throw CollationMismatch();
            }
            finally { _monitor.ReleaseTransaction(transaction); }
        }
    }
}
