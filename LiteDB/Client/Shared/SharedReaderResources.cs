using System;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    internal static class SharedReaderResources
    {
        // Never return the cursor, engine, settings, caller streams or callbacks.
        // MemoryCache owns only page storage and its own cache bookkeeping.
        // Transfer the built-in reader's no-op finalizer to the cleanup owner:
        // Dispose(false) would otherwise mark it disposed before the worker can
        // release its enumerator. User-provided readers retain their contract.
        internal static MemoryCache Preserve(IBsonDataReader reader)
        {
            if (reader is BsonDataReader bson && reader.GetType() == typeof(BsonDataReader))
            {
                var cache = bson.SharedPageCache;
                GC.SuppressFinalize(bson);
                return cache;
            }
            if (reader is PrefixedDataReader prefix) return Preserve(prefix.InnerReader);
            return null;
        }
    }
}
