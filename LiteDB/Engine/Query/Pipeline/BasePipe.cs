using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Abstract class with workflow method to be used in pipeline implementation
    /// </summary>
    internal abstract class BasePipe
    {
        protected readonly TransactionService _transaction;
        protected readonly IDocumentLookup _lookup;
        protected readonly SortDisk _tempDisk;
        protected readonly EnginePragmas _pragmas;
        protected readonly uint _maxItemsCount;

        public BasePipe(TransactionService transaction, IDocumentLookup lookup, SortDisk tempDisk, EnginePragmas pragmas, uint maxItemsCount)
        {
            _transaction = transaction;
            _lookup = lookup;
            _tempDisk = tempDisk;
            _pragmas = pragmas;
            _maxItemsCount = maxItemsCount;
        }

        /// <summary>
        /// Abstract method to be implement according pipe workflow
        /// </summary>
        public abstract IEnumerable<BsonDocument> Pipe(IEnumerable<IndexNode> nodes, QueryPlan query);

        // load documents from document loader
        protected IEnumerable<BsonDocument> LoadDocument(IEnumerable<IndexNode> nodes)
        {
            foreach (var node in nodes)
            {
                yield return _lookup.Load(node);

                // check if transaction all full of pages to clear before continue
                _transaction.Safepoint();
            }
        }

        /// <summary>
        /// INCLUDE: Do include in result document according path expression - Works only with DocumentLookup
        /// </summary>
        protected IEnumerable<BsonDocument> Include(IEnumerable<BsonDocument> source, BsonExpression path)
        {
            // cached services
            string last = null;
            Snapshot snapshot = null;
            IndexService indexer = null;
            DataService data = null;
            CollectionIndex index = null;
            IDocumentLookup lookup = null;

            foreach (var doc in source)
            {
                foreach (var value in path.Execute(doc, _pragmas.Collation)
                                        .Where(x => x.IsDocument || x.IsArray)
                                        .ToList())
                {
                    // if value is document, convert this ref document into full document (do another query)
                    if (value.IsDocument)
                    {
                        DoInclude(value.AsDocument);
                    }
                    else
                    {
                        // if value is array, do same per item
                        foreach(var item in value.AsArray
                            .Where(x => x.IsDocument)
                            .Select(x => x.AsDocument))
                        {
                            DoInclude(item);
                        }
                    }

                }

                yield return doc;
            }

            void DoInclude(BsonDocument value)
            {
                // works only if is a document
                var refId = value["$id"];
                var refCol = value["$ref"];

                // if has no reference, just go out
                if (refId.IsNull || !refCol.IsString) return;

                // do some cache re-using when is same $ref (almost always is the same $ref collection)
                if (last != refCol.AsString)
                {
                    last = refCol.AsString;

                    // initialize services
                    snapshot = _transaction.CreateSnapshot(LockMode.Read, last, false);
                    indexer = new IndexService(snapshot, _pragmas.Collation, _maxItemsCount);
                    data = new DataService(snapshot, _maxItemsCount);

                    lookup = new DatafileLookup(data, _pragmas.UtcDate, null);

                    index = snapshot.CollectionPage?.PK;
                }

                // fill only if index and ref node exists
                if (index != null)
                {
                    var node = indexer.Find(index, refId, false, Query.Ascending);

                    if (node != null)
                    {
                        // load document based on dataBlock position
                        var refDoc = lookup.Load(node);

                        //do not remove $id
                        value.Remove("$ref");

                        // copy values from refDocument into current documet (except _id - will keep $id)
                        foreach (var element in refDoc.Where(x => x.Key != "_id"))
                        {
                            value[element.Key] = element.Value;
                        }
                    }
                    else
                    {
                        // set in ref document that was not found
                        value["$missing"] = true;
                    }
                }
            }
        }

        /// <summary>
        /// WHERE: Filter document according expression. Expression must be an Bool result
        /// </summary>
        protected IEnumerable<BsonDocument> Filter(IEnumerable<BsonDocument> source, BsonExpression expr)
        {
            foreach(var doc in source)
            {
                // checks if any result of expression is true
                var result = expr.ExecuteScalar(doc, _pragmas.Collation);

                if(result.IsBoolean && result.AsBoolean)
                {
                    yield return doc;
                }
            }
        }

        /// <summary>
        /// ORDER BY: Sort documents according orderby expression and order asc/desc
        /// </summary>
        protected IEnumerable<BsonDocument> OrderBy(IEnumerable<BsonDocument> source, BsonExpression expr, int order, int offset, int limit)
        {
            var keyValues = source
                .Select(x => new KeyValuePair<BsonValue, PageAddress>(expr.ExecuteScalar(x, _pragmas.Collation), x.RawId));

            using (var sorter = new SortService(_tempDisk, order, _pragmas))
            {
                sorter.Insert(keyValues);

                LOG($"sort {sorter.Count} keys in {sorter.Containers.Count} containers", "SORT");

                var result = sorter.Sort().Skip(offset).Take(limit);

                foreach (var keyValue in result)
                {
                    var doc = _lookup.Load(keyValue.Value);

                    yield return doc;
                }
            }
        }
    }
}