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
        private readonly string _name;
        private readonly IDocumentLookup _documentLookup;
        private readonly bool _utcDate;

        public IndexLookup(string name, IDocumentLookup documentLookup, bool utcDate)
        {
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
            if (ContainsDate(node.Key)) return this.Load(node.DataBlock);

            var doc = new BsonDocument
            {
                [_name] = node.Key,
            };

            doc.RawId = node.DataBlock;

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
            // RawId is a data-block address, including sort and aggregate replay.
            var document = _documentLookup.Load(rawId);
            return _utcDate ? document : ConvertDates(document).AsDocument;
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
