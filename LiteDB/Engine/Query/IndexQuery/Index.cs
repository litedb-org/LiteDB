using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Class that implement higher level of index search operations (equals, greater, less, ...)
    /// </summary>
    internal abstract class Index
    {
        /// <summary>
        /// Index name
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Get/Set index order
        /// </summary>
        public int Order { get; set; }

        // Set only when the planner matched a scalar IR expression to the stored
        // index definition. No catalog parsing or persistent format flag is needed.
        internal bool SingleKeyPerDocument { get; set; }

        // Updates can move a document into a later part of the same secondary
        // scan, even when its index definition permits only one key at a time.
        internal bool ForUpdate { get; set; }

        internal Index(string name, int order)
        {
            this.Name = name;
            this.Order = order;
        }

        #region Executing Index Search

        /// <summary>
        /// Calculate cost based on type/value/collection - Lower is best (1)
        /// </summary>
        public abstract uint GetCost(CollectionIndex index);

        /// <summary>
        /// Abstract method that must be implement for index seek/scan - Returns IndexNodes that match with index
        /// </summary>
        public abstract IEnumerable<IndexNode> Execute(IndexService indexer, CollectionIndex index);

        /// <summary>
        /// Find witch index will be used and run Execute method
        /// </summary>
        public virtual IEnumerable<IndexNode> Run(CollectionPage col, IndexService indexer)
        {
            // get index for this query
            var index = col.GetCollectionIndex(this.Name);

            if (index == null) throw LiteException.IndexNotFound(this.Name);

            var nodes = this.Execute(indexer, index);
            // Primary and unique indexes have one key/node per document: index
            // creation rejects unique multikey expressions. A matched scalar IR
            // expression proves the same guarantee. IN already deduplicates seek
            // values; other scans retain their multikey address filtering.
            return index.Slot == 0 || (!ForUpdate && (index.Unique || SingleKeyPerDocument))
                ? nodes : nodes.DistinctBy(x => x.DataBlock, null);
        }

        public IEnumerable<IndexNode> Run(CollectionPage col, IndexService indexer, bool forUpdate)
        {
            this.ForUpdate = forUpdate;
            return this.Run(col, indexer);
        }

        #endregion
    }
}
