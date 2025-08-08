using System;
using System.Collections.Generic;

using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal static class RebuildHelpers
    {
        internal readonly struct ScannedDoc
        {
            public ScannedDoc(PageAddress block, BsonDocument doc)
            {
                Block = block;
                Doc = doc;
            }

            public PageAddress Block { get; }
            public BsonDocument Doc { get; }
        }

        public static IEnumerable<ScannedDoc> ScanDocuments(Snapshot snapshot, DataService data, uint maxItemsCount)
        {
            if (snapshot.CollectionPage == null) yield break;

            var counter = 0u;

            foreach (var page in snapshot.EnumerateDataPages())
            {
                foreach (var addr in page.GetBlocks())
                {
                    ENSURE(counter++ < maxItemsCount, "Detected loop in ScanDocuments({0})", snapshot.CollectionName);

                    using (var reader = new BufferReader(data.Read(addr)))
                    {
                        var result = reader.ReadDocument();
                        if (result.Fail) throw result.Exception;

                        yield return new ScannedDoc(addr, result.Value);
                    }
                }
            }
        }

        public static void EnsureIndexFromDataScan(
            Snapshot snapshot,
            string indexName,
            BsonExpression expression,
            bool unique,
            IndexService indexer,
            DataService data,
            Action safepoint)
        {
            var index = indexer.CreateIndex(indexName, expression.Source, unique);
            var pk = snapshot.CollectionPage.PK;
            var collation = indexer.Collation;

            foreach (var item in ScanDocuments(snapshot, data, uint.MaxValue))
            {
                var doc = item.Doc;
                var id = doc["_id"];

                if (id.IsNull || id.IsMinValue || id.IsMaxValue) continue;
                var pkNode = indexer.Find(pk, id, false, Query.Ascending);
                if (pkNode == null) continue;

                IndexNode last = null;
                IndexNode first = null;

                var keys = expression.GetIndexKeys(doc, collation);

                foreach (var key in keys)
                {
                    var node = indexer.AddNode(index, key, item.Block, last);
                    if (first == null) first = node;
                    last = node;
                }

                if (first != null)
                {
                    last.SetNextNode(pkNode.NextNode);
                    pkNode.SetNextNode(first.Position);
                }

                safepoint?.Invoke();
            }
        }

        public static bool ValidatePkNoCycle(IndexService indexer, CollectionIndex pk, out int count, uint maxItemsCount)
        {
            count = 0;

            var seen = new HashSet<PageAddress>();

            try
            {
                foreach (var node in indexer.FindAll(pk, Query.Ascending))
                {
                    if (!seen.Add(node.Position)) return false;

                    count++;
                    if (count > maxItemsCount) return false;
                }

                return true;
            }
            catch (LiteException)
            {
                return false;
            }
        }
    }
}
