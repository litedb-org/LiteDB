using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal class SysPageList : SystemCollection
    {
        private readonly HeaderPage _header;
        private readonly TransactionMonitor _monitor;
        private Dictionary<uint, string> _collections;

        public SysPageList(HeaderPage header, TransactionMonitor monitor) : base("$page_list")
        {
            _header = header;
            _monitor = monitor;
        }

        public override IEnumerable<BsonDocument> Input(BsonValue options)
        {
            var pageID = GetOption(options, "pageID");

            // get any transaction from current thread ID
            var transaction = _monitor.GetThreadTransaction();
            var snapshot = transaction.CreateSnapshot(LockMode.Read, "$", false);

            _collections = _header.GetCollections().ToDictionary(x => x.Value, x => x.Key);

            var result = pageID != null ?
                this.GetList((uint)pageID.AsInt32, null, transaction, snapshot) :
                this.GetAllList(transaction, snapshot);

            foreach (var page in result)
            {
                yield return page;
            }
        }

        private IEnumerable<BsonDocument> GetAllList(TransactionService transaction, Snapshot snapshot)
        {
            // get empty page list, from header
            foreach (var page in this.GetList(_header.FreeEmptyPageList, null, transaction, snapshot))
            {
                yield return page;
            }

            // get lists from data pages/index list
            foreach (var collection in _collections)
            {
                var snap = transaction.CreateSnapshot(LockMode.Read, collection.Value, false);

                for (var slot = 0; slot < PAGE_FREE_LIST_SLOTS; slot++)
                {
                    var result = this.GetList(snap.CollectionPage.FreeDataPageList[slot], null, transaction, snap);

                    foreach (var page in result)
                    {
                        yield return page;
                    }
                }

                var indexes = snap.CollectionPage.GetCollectionIndexes().ToArray();

                foreach (var index in indexes)
                {
                    var result = this.GetList(index.FreeIndexPageList, index.Name, transaction, snap);

                    foreach (var page in result)
                    {
                        yield return page;
                    }

                }
            }
        }

        private IEnumerable<BsonDocument> GetList(uint pageID, string indexName, TransactionService transaction, Snapshot snapshot)
        {
            if (pageID == uint.MaxValue) yield break;

            var page = snapshot.GetPage<BasePage>(pageID);

            while (page != null)
            {
                _collections.TryGetValue(page.ColID, out var collection);

                yield return new BsonDocument
                {
                    ["pageID"] = (int)page.PageID,
                    ["pageType"] = page.PageType.ToString(),
                    ["slot"] = (int)page.PageListSlot,
                    ["collection"] = collection,
                    ["index"] = indexName,
                    ["freeBytes"] = page.FreeBytes,
                    ["itemsCount"] = (int)page.ItemsCount
                };

                if (page.NextPageID == uint.MaxValue) break;

                transaction.Safepoint();

                page = snapshot.GetPage<BasePage>(page.NextPageID);
            }
        }
    }
}