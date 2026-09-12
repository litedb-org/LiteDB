using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Represent a simple structure to store added/removed pages in a transaction. One instance per transaction
    /// [SingleThread]
    /// </summary>
    internal class TransactionPages
    {
        /// <summary>
        /// Get how many pages are involved in this transaction across all snapshots - Will be clear when get MAX_TRANSACTION_SIZE
        /// </summary>
        private int _transactionSize;

        public int TransactionSize
        {
            get => Volatile.Read(ref _transactionSize);
            set => Volatile.Write(ref _transactionSize, value);
        }

        /// <summary>
        /// Contains all dirty pages already persist in LOG file (used in all snapshots). Store in [uint, PagePosition] to reuse same method in save pages into log and get saved page positions on log
        /// </summary>
        public Dictionary<uint, PagePosition> DirtyPages { get; } = new Dictionary<uint, PagePosition>();

        /// <summary>
        /// Handle created pages during transaction (for rollback) - Is a list because order is important
        /// </summary>
        public List<uint> NewPages { get; } = new List<uint>();

        /// <summary>
        /// First deleted pageID 
        /// </summary>
        public uint FirstDeletedPageID { get; set; } = uint.MaxValue;

        /// <summary>
        /// Last deleted pageID
        /// </summary>
        public uint LastDeletedPageID { get; set; } = uint.MaxValue;

        /// <summary>
        /// Get deleted page count
        /// </summary>
        public int DeletedPages { get; set; }

        /// <summary>
        /// Callback function to modify header page on commit
        /// </summary>
        public event Action<HeaderPage> Commit;

        /// <summary>
        /// Run Commit event
        /// </summary>
        public void OnCommit(HeaderPage header)
        {
            this.Commit?.Invoke(header);
        }

        /// <summary>
        /// Detect if this transaction will need persist header page (has added/deleted pages or added/deleted collections)
        /// </summary>
        public bool HeaderChanged =>
            this.NewPages.Count > 0 ||
            this.DeletedPages > 0 || 
            this.Commit != null;
    }
}