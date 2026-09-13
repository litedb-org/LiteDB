using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        private IEnumerable<BsonDocument> SysDatabase()
        {
            var version = typeof(LiteEngine).GetTypeInfo().Assembly.GetName().Version;

            yield return new BsonDocument
            {
                ["name"] = _disk.GetName(FileOrigin.Data),
                ["encrypted"] = _settings.Password != null,
                ["readOnly"] = _settings.ReadOnly,

                ["lastPageID"] = (int)_header.LastPageID,
                ["freeEmptyPageID"] = (int)_header.FreeEmptyPageList,

                ["creationTime"] = _header.CreationTime,

                ["dataFileSize"] = (int)_disk.GetFileLength(FileOrigin.Data),
                ["logFileSize"] = (int)_disk.GetFileLength(FileOrigin.Log),

                ["currentReadVersion"] = _walIndex.CurrentReadVersion,
                ["lastTransactionID"] = _walIndex.LastTransactionID,
                ["engine"] = $"litedb-ce-v{version.Major}.{version.Minor}.{version.Build}",

                ["pragmas"] = new BsonDocument(_header.Pragmas.Pragmas.ToDictionary(x => x.Name, x => x.Get())),

                ["cache"] = new BsonDocument
                {
                    ["extendSegments"] = _disk.Cache.ExtendSegments,
                    ["extendPages"] = _disk.Cache.ExtendPages,
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