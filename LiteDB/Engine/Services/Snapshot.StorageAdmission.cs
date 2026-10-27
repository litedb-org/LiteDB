namespace LiteDB.Engine
{
    internal partial class Snapshot
    {
        private int _compactDocumentHash;
        private int _compactMisses;
        private int _compactBackoff;

        internal bool ShouldPrepareCompact(BsonDocument document, int bsonLength)
        {
            if (!CompactStorage || bsonLength < 64) return false;
            var scalar = !CompactDocumentWriter.HasContainers(document);
            if (!scalar) { _compactDocumentHash = document["_id"].GetHashCode(); return true; }
            // Field-name removal cannot justify copying a large scalar payload
            // when its maximum metadata saving is less than ten percent.
            if (bsonLength >= Constants.PAGE_SIZE)
            {
                var namesLength = 0;
                foreach (var element in new CompactDocumentElements(document))
                    namesLength += StringEncoding.UTF8.GetByteCount(element.Key) + 1;
                if (namesLength < bsonLength / 10) return false;
            }
            // Periodically resample dynamic dictionaries instead of paying a full
            // shape-selection pass for every unique row. This affects admission only.
            if (_compactBackoff > 0) { _compactBackoff--; return false; }
            _compactDocumentHash = document["_id"].GetHashCode();
            return true;
        }

        internal void ObserveCompactResult(bool useful)
        {
            if (useful) _compactMisses = 0;
            else if (++_compactMisses >= 16)
            {
                _compactMisses = 0;
                _compactBackoff = 128;
            }
        }
    }
}
