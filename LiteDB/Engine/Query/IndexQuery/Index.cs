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
            // Scalar expressions publish one key per document. These scans visit
            // each node once, so retaining every document address is unnecessary.
            // IN can revisit collation-equivalent keys; enumerable indexes can
            // have several keys per document. Both still require deduplication.
            if (index.BsonExpr.IsScalar &&
                (this is IndexAll || this is IndexEquals || this is IndexRange ||
                 this is IndexScan || this is IndexLike)) return nodes;
            return nodes.DistinctBy(x => x.DataBlock, null);
        }

        #endregion
    }
}