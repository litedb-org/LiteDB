﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        private IEnumerable<BsonDocument> SysDatabase()
        {
            yield return new BsonDocument
            {
                ["name"] = _disk.GetName(FileOrigin.Data),
                ["encrypted"] = _settings.Password != null,
                ["readOnly"] = _settings.ReadOnly,

                ["lastPageID"] = (int)_header.LastPageID,
                ["freeEmptyPageID"] = (int)_header.FreeEmptyPageList,

                ["creationTime"] = _header.CreationTime,

                ["dataFileSize"] = (int)_disk.GetLength(FileOrigin.Data),
                ["logFileSize"] = (int)_disk.GetLength(FileOrigin.Log),
                ["asyncQueueLength"] = _disk.Queue.Length,

                ["currentReadVersion"] = _walIndex.CurrentReadVersion,
                ["lastTransactionID"] = _walIndex.LastTransactionID,

                ["pragmas"] = new BsonDocument(_header.Pragmas.Pragmas.ToDictionary(x => x.Name, x => x.Get())),

                ["cache"] = new BsonDocument
                {
                    ["extendSegments"] = _disk.Cache.ExtendSegments,
                    ["memoryUsage"] =
                    (_disk.Cache.ExtendSegments * MEMORY_SEGMENT_SIZE * PAGE_SIZE) +
                    (40 * (_disk.Cache.ExtendSegments * MEMORY_SEGMENT_SIZE)),
                    ["freePages"] = _disk.Cache.FreePages,
                    ["readablePages"] = _disk.Cache.GetPages().Count,
                    ["writablePages"] = _disk.Cache.WritablePages,
                    ["pagesInUse"] = _disk.Cache.PagesInUse,
                },

                ["transactions"] = new BsonDocument
                {
                    ["open"] = _monitor.Transactions.Count,
                    ["maxOpenTransactions"] = MAX_OPEN_TRANSACTIONS,
                    ["initialTransactionSize"] = _monitor.InitialSize,
                    ["availableSize"] = _monitor.FreePages
                }

            };
        }
    }
}