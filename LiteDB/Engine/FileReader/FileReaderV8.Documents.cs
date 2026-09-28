using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class FileReaderV8
    {
        // Documents read only up to their damage, per collection, and the _id of every complete one.
        private readonly Dictionary<string, List<(BsonDocument Document, PageInfo Page)>> _salvaged =
            new Dictionary<string, List<(BsonDocument, PageInfo)>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<BsonValue>> _completeIDs =
            new Dictionary<string, HashSet<BsonValue>>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<BsonDocument> GetDocuments(string collection)
        {
            if (!_collections.ContainsKey(collection)) yield break;

            var colID = _collections[collection];

            if (!_collectionsDataPages.ContainsKey(colID)) yield break;

            var dataPages = _collectionsDataPages[colID];
            var uniqueIDs = _completeIDs[collection] = new HashSet<BsonValue>();
            var salvaged = _salvaged[collection] = new List<(BsonDocument, PageInfo)>();

            foreach (var dataPage in dataPages)
            {
                var page = this.ReadPage(dataPage, out var pageInfo);

                if (page.Fail)
                {
                    this.HandleError(page.Exception, pageInfo);
                    continue;
                }

                var buffer = page.Value.Buffer;
                var itemsCount = page.Value.ItemsCount;
                var highestIndex = page.Value.HighestIndex;

                // no items
                if (itemsCount == 0 || highestIndex == byte.MaxValue) continue;

                for (int i = 0; i <= highestIndex; i++)
                {
                    BsonDocument doc;

                    // try/catch block per dataBlock extend=false
                    try
                    {
                        // resolve slot address
                        var positionAddr = BasePage.CalcPositionAddr((byte)i);
                        var lengthAddr = BasePage.CalcLengthAddr((byte)i);

                        // read segment position/length
                        var position = buffer.ReadUInt16(positionAddr);
                        var length = buffer.ReadUInt16(lengthAddr);

                        // empty slot
                        if (position == 0) continue;

                        ENSURE(position > 0 && length > 0, "Invalid footer ref position {0} with length {1}", position, length);
                        ENSURE(position + length < PAGE_SIZE, "Invalid footer ref position {0} with length {1}", position, length);

                        // get segment slice
                        var segment = buffer.Slice(position, length);
                        var extend = segment.ReadBool(DataBlock.P_EXTEND);
                        var nextBlock = segment.ReadPageAddress(DataBlock.P_NEXT_BLOCK);
                        var data = segment.Slice(DataBlock.P_BUFFER, segment.Count - DataBlock.P_BUFFER);

                        if (extend) continue; // ignore extend block (start only in first data block)

                        // merge all data block content into a single memory stream and read bson document
                        using (var mem = new MemoryStream())
                        {
                            // write first block
                            mem.Write(data.Array, data.Offset, data.Count);

                            var visited = new HashSet<PageAddress>();
                            while (nextBlock.IsEmpty == false)
                            {
                                if (!visited.Add(nextBlock) || mem.Length > MAX_DOCUMENT_SIZE) throw new InvalidDataException("Document block cycle/size limit.");
                                // read next page block
                                var nextPage = this.ReadPage(nextBlock.PageID, out pageInfo);

                                if (nextPage.Fail) throw nextPage.Exception;

                                var nextBuffer = nextPage.Value.Buffer;

                                // make page validations
                                ENSURE(nextPage.Value.PageType == PageType.Data, "Invalid PageType (excepted Data, get {0})", nextPage.Value.PageType);
                                ENSURE(nextPage.Value.ColID == colID, "Invalid ColID in this page (expected {0}, get {1})", colID, nextPage.Value.ColID);
                                ENSURE(nextPage.Value.ItemsCount > 0, "Page with no items count");

                                // read slot address
                                positionAddr = BasePage.CalcPositionAddr(nextBlock.Index);
                                lengthAddr = BasePage.CalcLengthAddr(nextBlock.Index);

                                // read segment position/length
                                position = nextBuffer.ReadUInt16(positionAddr);
                                length = nextBuffer.ReadUInt16(lengthAddr);

                                // empty slot
                                ENSURE(length > 0, "Last DataBlock request a next extend to {0}, but this block are empty footer", nextBlock);

                                // get segment slice
                                segment = nextBuffer.Slice(position, length);
                                extend = segment.ReadBool(DataBlock.P_EXTEND);
                                nextBlock = segment.ReadPageAddress(DataBlock.P_NEXT_BLOCK);
                                data = segment.Slice(DataBlock.P_BUFFER, segment.Count - DataBlock.P_BUFFER);

                                ENSURE(extend == true, "Next datablock always be an extend. Invalid data block {0}", nextBlock);

                                // write data on memorystream

                                mem.Write(data.Array, data.Offset, data.Count);
                            }

                            var docBytes = mem.ToArray();

                            // read all data array in bson document
                            using (var r = new BufferReader(docBytes, false))
                            {
                                var docResult = DocumentStorageCodec.Read(r, null, () => ReadSchemas(colID), false, collection, new PageAddress(dataPage, (byte)i));

                                if (docResult.Fail)
                                {
                                    this.HandleError(docResult.Exception, pageInfo);
                                    // Like released versions, keep the fields read before the damage
                                    // when they still identify the document; the error stays recorded.
                                    // They are offered after every complete document (GetSalvagedDocuments).
                                    if (IsSalvageable(docResult.Value)) salvaged.Add((docResult.Value, pageInfo));
                                    continue;
                                }

                                var id = docResult.Value["_id"];

                                ENSURE(!(id == BsonValue.Null || id == BsonValue.MinValue || id == BsonValue.MaxValue), "Invalid _id value: {0}", id);
                                ENSURE(uniqueIDs.Contains(id) == false, "Duplicated _id value: {0}", id);

                                uniqueIDs.Add(id);
                                doc = docResult.Value;
                            }
                        }
                    }
                    // try/catch block per dataBlock extend=false
                    catch (Exception ex)
                    {
                        this.HandleError(ex, pageInfo);
                        doc = null;
                    }

                    if (doc != null)
                    {
                        yield return doc;
                    }
                }
            }
        }

        /// <summary>
        /// The readable fields of the damaged documents of a collection whose documents were read,
        /// except those whose _id belongs to a complete document or an earlier partial one.
        /// </summary>
        public IEnumerable<BsonDocument> GetSalvagedDocuments(string collection)
        {
            if (!_salvaged.TryGetValue(collection, out var salvaged)) yield break;

            var complete = _completeIDs[collection];
            var kept = new HashSet<BsonValue>();
            foreach (var (document, page) in salvaged)
            {
                var id = document["_id"];
                if (complete.Contains(id) || !kept.Add(id))
                {
                    this.HandleError($"The readable part of damaged document {id} was not kept: another document has the same _id.", page);
                    continue;
                }
                yield return document;
            }
        }

        public void RejectSalvagedDocument(string collection, BsonDocument document, string reason)
        {
            var page = _salvaged[collection].First(x => ReferenceEquals(x.Document, document)).Page;
            this.HandleError($"The readable part of damaged document {document["_id"]} was not kept: {reason}", page);
        }

        private static bool IsSalvageable(BsonDocument partial)
        {
            if (partial == null || !partial.TryGetValue("_id", out var id)) return false;
            return !(id.IsNull || id.IsMinValue || id.IsMaxValue);
        }
    }
}
