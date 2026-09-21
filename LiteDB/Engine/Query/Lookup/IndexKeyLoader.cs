using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Implement lookup based only in index Key
    /// </summary>
    internal class IndexLookup : IDocumentLookup
    {
        private readonly IndexService _indexer;
        private readonly string _name;
        private readonly IDocumentLookup _documentLookup;
        private readonly bool _utcDate;

        public IndexLookup(IndexService indexer, string name, IDocumentLookup documentLookup, bool utcDate)
        {
            _indexer = indexer;
            _name = name;
            _documentLookup = documentLookup;
            _utcDate = utcDate;
        }

        public BsonDocument Load(IndexNode node)
        {
            ENSURE(node.DataBlock.IsEmpty == false, "Never should be empty rawid");

            // Index keys use legacy date decoding for comparisons. Read date-bearing
            // projections from the document to honor UtcDate and preserve the stored
            // value without changing the interpretation/order of existing indexes.
            if (ContainsDate(node.Key))
            {
                var document = _documentLookup.Load(node.DataBlock);
                document = _utcDate ? document : ConvertDates(document).AsDocument;
                document.RawId = node.Position;
                return document;
            }

            var doc = new BsonDocument
            {
                [_name] = node.Key,
            };

            // Sort and aggregate replay return this address to this same lookup.
            // It loads index nodes, so retain their positions rather than data blocks.
            doc.RawId = node.Position;

            return doc;
        }

        private static bool ContainsDate(BsonValue value)
        {
            if (value.IsDateTime) return true;
            if (value.IsDocument)
            {
                foreach (var item in value.AsDocument)
                    if (ContainsDate(item.Value)) return true;
            }
            else if (value.IsArray)
            {
                foreach (var item in value.AsArray)
                    if (ContainsDate(item)) return true;
            }
            return false;
        }

        public BsonDocument Load(PageAddress rawId)
        {
            return this.Load(_indexer.GetNode(rawId));
        }

        private static BsonValue ConvertDates(BsonValue value)
        {
            if (value.IsDateTime)
            {
                var date = value.AsDateTime;
                if (date == DateTime.MinValue || date == DateTime.MaxValue) return value;
                return BsonValue.FromDecodedDateTime(date.ToLocalTime());
            }
            if (value.IsDocument)
            {
                var source = value.AsDocument;
                var document = new BsonDocument { RawId = source.RawId };
                foreach (var item in source) document[item.Key] = ConvertDates(item.Value);
                return document;
            }
            if (value.IsArray)
            {
                var array = new BsonArray();
                foreach (var item in value.AsArray) array.Add(ConvertDates(item));
                return array;
            }
            return value;
        }
    }
}
