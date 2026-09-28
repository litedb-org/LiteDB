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

            var transactions = _monitor.Transactions.Select(x => new BsonDocument
            {
                ["transactionID"] = (int)x.TransactionID,
                ["pages"] = x.Pages.TransactionSize
            }).ToArray();

            // First: its data sync may fail, which this read records instead of throwing (decision 6).
            var walKept = _disk.WalKeptReport;
            // The failure the engine reopened read-only after, or one recorded on it that its next call
            // stops it for (and reopens it read-only).
            var failure = _settings.WriteFailure ?? _state.WriteFailure;

            yield return new BsonDocument
            {
                ["name"] = _disk.GetName(FileOrigin.Data),
                ["encrypted"] = _settings.Password != null,
                ["readOnly"] = _settings.ReadOnly || failure != null,
                // Why a writable open opened read-only instead (the data file cannot sync); null otherwise.
                ["readOnlyReason"] = _settings.ReadOnlyCause ?? failure?.ToString(),
                // The write or sync failure after which the engine continues read-only (decision 6).
                ["writeFailure"] = failure?.ToDocument() ?? BsonValue.Null,

                ["lastPageID"] = (int)_header.LastPageID,
                ["freeEmptyPageID"] = (int)_header.FreeEmptyPageList,

                ["creationTime"] = _header.CreationTime,

                ["dataFileSize"] = _disk.GetFileLength(FileOrigin.Data),
                ["logFileSize"] = _disk.GetFileLength(FileOrigin.Log),
                // durableLogFlush: the mode commits ran in so far; false once a log write or sync failed.
                ["durableLogFlush"] = _disk.IsLogFlushDurable,
                ["walKept"] = walKept,
                // How large a kept log may grow before writes throw (decision 4).
                ["walLimit"] = _settings.WalLimit,
                ["checksums"] = _disk.ChecksumsEnabled,
                ["checksumCoverage"] = _disk.ChecksumCoverage,
                ["legacyLastPageID"] = (long)_disk.LegacyLastPageID,
                ["recoveryDiscardedWalBytes"] = RecoveryReport?.DiscardedBytes ?? 0,
                ["recoveryInvalidWalTail"] = RecoveryReport?.InvalidTail ?? false,

                ["currentReadVersion"] = _walIndex.CurrentReadVersion,
                ["lastTransactionID"] = _walIndex.LastTransactionID,
                ["engine"] = $"litedb-ce-v{version.Major}.{version.Minor}.{version.Build}",

                ["pragmas"] = new BsonDocument(_header.Pragmas.Pragmas.ToDictionary(x => x.Name, x => x.Get())),

                ["cache"] = new BsonDocument
                {
                    ["memoryProfile"] = _settings.MemoryProfile.ToString(),
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
                    ["transactionPages"] = new BsonArray(transactions)
                }

            };
        }
    }
}
