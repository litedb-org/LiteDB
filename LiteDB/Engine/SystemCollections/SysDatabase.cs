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
            var transactions = _monitor.GetTransactionsSnapshot();

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
                    ["limitBytes"] = _disk.Cache.LimitBytes,
                    ["limitPagesRounded"] = _disk.Cache.LimitPagesRounded,
                    ["allocatedBytes"] = _disk.Cache.AllocatedBytes,
                    ["segments"] = _disk.Cache.Segments,
                    ["totalPages"] = _disk.Cache.TotalPages,
                    ["readablePages"] = _disk.Cache.ReadablePages,
                    ["idleReadablePages"] = _disk.Cache.IdleReadablePages,
                    ["loadingPages"] = _disk.Cache.LoadingPages,
                    ["pinnedPages"] = _disk.Cache.PinnedPages,
                    ["retainedBySegments"] = _disk.Cache.RetainedBySegments,
                    ["evictedPages"] = _disk.Cache.EvictedPages,
                    ["releasedSegments"] = _disk.Cache.ReleasedSegments,
                    ["overflowSegments"] = _disk.Cache.OverflowSegments,
                    ["framesExamined"] = _disk.Cache.FramesExamined,
                    ["budgetExceeded"] = _disk.Cache.BudgetExceeded,
                    ["lostFrames"] = _disk.Cache.LostFrames,
                    ["hits"] = _disk.Cache.Hits,
                    ["misses"] = _disk.Cache.Misses,
                    ["compiledExpressions"] = BsonExpression.CompiledExpressionCount,

                    // Compatibility aliases retained for existing diagnostics.
                    ["extendSegments"] = _disk.Cache.ExtendSegments,
                    ["extendPages"] = _disk.Cache.ExtendPages,
                    ["freePages"] = _disk.Cache.FreePages,
                    ["writablePages"] = _disk.Cache.WritablePages,
                    ["pagesInUse"] = _disk.Cache.PagesInUse,
                },

                ["transactions"] = new BsonDocument
                {
                    ["open"] = transactions.Length,
                    ["maxOpenTransactions"] = MAX_OPEN_TRANSACTIONS,
                    ["transactionPageLimit"] = _monitor.TransactionPageLimit,
                    ["transactionPages"] = new BsonArray(transactions.Select(x => new BsonDocument
                    {
                        ["transactionID"] = (int)x.TransactionID,
                        ["pages"] = x.Pages.TransactionSize
                    }))
                }

            };
        }
    }
}
