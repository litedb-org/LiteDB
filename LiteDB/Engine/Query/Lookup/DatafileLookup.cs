using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Implement basic document loader based on data service/bson reader
    /// </summary>
    internal class DatafileLookup : IDocumentLookup
    {
        protected readonly DataService _data;
        protected readonly bool _utcDate;
        protected readonly HashSet<string> _fields;
        private BufferReader _materializer;

        public DatafileLookup(DataService data, bool utcDate, HashSet<string> fields)
        {
            _data = data;
            _utcDate = utcDate;
            _fields = fields;
        }

        public virtual BsonDocument Load(IndexNode node)
        {
            ENSURE(node.DataBlock != PageAddress.Empty, "data block must be a valid block address");

            return this.Load(node.DataBlock);
        }

        public virtual BsonDocument Load(PageAddress rawId)
        {
            BorrowedQueryDiagnostics.Materialized();

            // A lookup belongs to one query and reads synchronously. Reuse its
            // cursor, clearing the borrowed page reference after every document.
            var reader = _materializer;
            if (reader == null) _materializer = reader = new BufferReader(_data, _utcDate);
            else reader.FieldNames ??= new BsonFieldNameCache();
            try
            {
                reader.Reset(rawId);
                var doc = _data.ReadDocument(reader, _fields, _utcDate, rawId).GetValue();

                doc.RawId = rawId;

                return doc;
            }
            finally { reader.Dispose(); }
        }

        internal bool TryEvaluate(IndexNode node, BorrowedDocumentReader documentReader,
            BorrowedPredicateEvaluator predicate, BorrowedValueBuffer values,
            Collation collation, out bool result)
        {
            if (!this.ReadBorrowed(node, documentReader, values, predicate.SlotCount))
            {
                result = false;
                return false;
            }

            return predicate.TryEvaluate(values, collation, out result);
        }

        internal BorrowedDocumentReader CreateBorrowedReader(BorrowedPredicateEvaluator predicate)
        {
            return new BorrowedDocumentReader(predicate.Paths, _utcDate, _data);
        }

        internal BorrowedDocumentReader CreateBorrowedReader(BorrowedScalarEvaluator scalar)
        {
            return new BorrowedDocumentReader(scalar.Paths, _utcDate, _data);
        }

        internal BorrowedDocumentReader CreateBorrowedReader(BorrowedProjectionEvaluator projection)
        {
            return new BorrowedDocumentReader(projection.Paths, _utcDate, _data);
        }

        internal bool ReadBorrowed(IndexNode node, BorrowedDocumentReader documentReader,
            BorrowedValueBuffer values, int slotCount)
        {
            ENSURE(node.DataBlock != PageAddress.Empty, "data block must be a valid block address");

            values.Reset(slotCount);

            return documentReader.Read(node.DataBlock, values);
        }
    }
}
