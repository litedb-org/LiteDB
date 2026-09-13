using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Implement a Index service - Add/Remove index nodes on SkipList
    /// Based on: http://igoro.com/archive/skip-lists-are-fascinating/
    /// </summary>
    internal class IndexService
    {
        private readonly Snapshot _snapshot;
        private readonly Collation _collation;
        private readonly uint _maxItemsCount;

        public IndexService(Snapshot snapshot, Collation collation, uint maxItemsCount)
        {
            _snapshot = snapshot;
            _collation = collation;
            _maxItemsCount = maxItemsCount;
        }

        public Collation Collation => _collation;

        /// <summary>
        /// Create a new index and returns head page address (skip list)
        /// </summary>
        public CollectionIndex CreateIndex(string name, string expr, bool unique)
        {
            // get how many bytes needed for each head/tail (both has same size)
            var bytesLength = IndexNode.GetNodeLength(MAX_LEVEL_LENGTH, BsonValue.MinValue, out var keyLength);

            // get a new empty page (each index contains its own linked nodes)
            var indexPage = _snapshot.NewPage<IndexPage>();

            // create index ref
            var index = _snapshot.CollectionPage.InsertCollectionIndex(name, expr, unique);

            // insert head/tail nodes
            var head = indexPage.InsertIndexNode(index.Slot, MAX_LEVEL_LENGTH, BsonValue.MinValue, PageAddress.Empty, bytesLength);
            var tail = indexPage.InsertIndexNode(index.Slot, MAX_LEVEL_LENGTH, BsonValue.MaxValue, PageAddress.Empty, bytesLength);

            // link head-to-tail with double link list in first level
            head.SetNext(0, tail.Position);
            tail.SetPrev(0, head.Position);

            // add this new page in free list (slot 0)
            index.FreeIndexPageList = indexPage.PageID;
            indexPage.PageListSlot = 0;

            index.Head = head.Position;
            index.Tail = tail.Position;

            return index;
        }

        /// <summary>
        /// Insert a new node index inside an collection index. Flip coin to know level
        /// </summary>
        public IndexNode AddNode(CollectionIndex index, BsonValue key, PageAddress dataBlock, IndexNode last)
        {
            // do not accept Min/Max value as index key (only head/tail can have this value)
            if (key.IsMaxValue || key.IsMinValue)
            {
                throw LiteException.InvalidIndexKey($"BsonValue MaxValue/MinValue are not supported as index key");
            }

            // random level (flip coin mode) - return number between 1-32
            var levels = this.Flip();

            // call AddNode with key value
            return this.AddNode(index, key, dataBlock, levels, last);
        }

        /// <summary>
        /// Insert a new node index inside an collection index.
        /// </summary>
        private IndexNode AddNode(
            CollectionIndex index,
            BsonValue key,
            PageAddress dataBlock,
            byte insertLevels,
            IndexNode last)
        {
            // get a free index page for head note
            var bytesLength = IndexNode.GetNodeLength(insertLevels, key, out var keyLength);

            // test for index key maxlength
            if (keyLength > MAX_INDEX_KEY_LENGTH) throw LiteException.InvalidIndexKey($"Index key must be less than {MAX_INDEX_KEY_LENGTH} bytes.");

            var indexPage = _snapshot.GetFreeIndexPage(bytesLength, ref index.FreeIndexPageList);

            // create node in buffer
            var node = indexPage.InsertIndexNode(index.Slot, insertLevels, key, dataBlock, bytesLength);

            // now, let's link my index node on right place
            var leftNode = this.GetNode(index.Head);
            var counter = 0u;

            // scan from top left
            for (int currentLevel = MAX_LEVEL_LENGTH - 1; currentLevel >= 0; currentLevel--)
            {
                var right = leftNode.Next[currentLevel];

                // while: scan from left to right
                while (right.IsEmpty == false && right != index.Tail)
                {
                    ENSURE(counter++ < _maxItemsCount, "Detected loop in AddNode({0})", node.Position);

                    var rightNode = this.GetNode(right);

                    // read next node to compare
                    var diff = rightNode.Key.CompareTo(key, _collation);

                    if (diff == 0 && index.Unique) throw LiteException.IndexDuplicateKey(index.Name, key);

                    if (diff == 1) break; // stop going right

                    leftNode = rightNode;
                    right = rightNode.Next[currentLevel];
                }

                if (currentLevel <= (insertLevels - 1)) // level == length
                {
                    // prev: immediately before new node
                    // node: new inserted node
                    // next: right node from prev (where left is pointing)

                    var prev = leftNode.Position;
                    var next = leftNode.Next[currentLevel];

                    // if next is empty, use tail (last key)
                    if (next.IsEmpty) next = index.Tail;

                    // set new node pointer links with current level sibling
                    node.SetNext((byte)currentLevel, next);
                    node.SetPrev((byte)currentLevel, prev);

                    // fix sibling pointer to new node
                    leftNode.SetNext((byte)currentLevel, node.Position);

                    right = node.Next[currentLevel]; // next

                    var rightNode = this.GetNode(right);

                    // mark right page as dirty (after change PrevID)
                    rightNode.SetPrev((byte)currentLevel, node.Position);
                }
            }

            // if last node exists, create a single link list
            if (last != null)
            {
                ENSURE(last.NextNode == PageAddress.Empty, "last index node must point to null");

                // reload 'last' index node in case the IndexPage has gone through a defrag
                last = this.GetNode(last.Position);
                last.SetNextNode(node.Position);
            }

            // fix page position in free list slot
            _snapshot.AddOrRemoveFreeIndexList(node.Page, ref index.FreeIndexPageList);

            return node;
        }


        /// <summary>
        /// Flip coin (skipped list): returns how many levels the node will have (starts in 1, max of INDEX_MAX_LEVELS)
        /// </summary>
        public byte Flip()
        {
            byte levels = 1;

            for (int R = Randomizer.Next(); (R & 1) == 1; R >>= 1)
            {
                levels++;
                if (levels == MAX_LEVEL_LENGTH) break;
            }

            return levels;
        }

        /// <summary>
        /// Get a node inside a page using PageAddress - Returns null if address IsEmpty
        /// </summary>
        public IndexNode GetNode(PageAddress address)
        {
            if (address.PageID == uint.MaxValue) return null;

            var indexPage = _snapshot.GetPage<IndexPage>(address.PageID);

            return indexPage.GetIndexNode(address.Index);
        }

        /// <summary>
        /// Gets all node list from passed nodeAddress (forward only)
        /// </summary>
        public IEnumerable<IndexNode> GetNodeList(PageAddress nodeAddress)
        {
            var node = this.GetNode(nodeAddress);
            var counter = 0u;

            while (node != null)
            {
                ENSURE(counter++ < _maxItemsCount, "Detected loop in GetNodeList({0})", nodeAddress);

                yield return node;

                node = this.GetNode(node.NextNode);
            }
        }

        /// <summary>
        /// Deletes all indexes nodes from pkNode
        /// </summary>
        public void DeleteAll(PageAddress pkAddress)
        {
            var node = this.GetNode(pkAddress);
            var indexes = _snapshot.CollectionPage.GetCollectionIndexesSlots();
            var counter = 0u;

            while (node != null)
            {
                ENSURE(counter++ < _maxItemsCount, "Detected loop in DeleteAll({0})", pkAddress);

                this.DeleteSingleNode(node, indexes[node.Slot]);

                // move to next node
                node = this.GetNode(node.NextNode);
            }
        }

        /// <summary>
        /// Deletes all list of nodes in toDelete - fix single linked-list and return last non-delete node
        /// </summary>
        public IndexNode DeleteList(PageAddress pkAddress, HashSet<PageAddress> toDelete)
        {
            var last = this.GetNode(pkAddress);
            var node = this.GetNode(last.NextNode); // starts in first node after PK
            var indexes = _snapshot.CollectionPage.GetCollectionIndexesSlots();
            var counter = 0u;

            while (node != null)
            {
                ENSURE(counter++ < _maxItemsCount, "Detected loop in DeleteList({0})", pkAddress);

                if (toDelete.Contains(node.Position))
                {
                    this.DeleteSingleNode(node, indexes[node.Slot]);

                    // fix single-linked list from last non-delete delete
                    last.SetNextNode(node.NextNode);
                }
                else
                {
                    // last non-delete node to set "NextNode"
                    last = node;
                }

                // move to next node
                node = this.GetNode(node.NextNode);
            }

            return last;
        }

        /// <summary>
        /// Delete a single index node - fix tree double-linked list levels
        /// </summary>
        private void DeleteSingleNode(IndexNode node, CollectionIndex index)
        {
            for (int i = node.Levels - 1; i >= 0; i--)
            {
                // get previous and next nodes (between my deleted node)
                var prevNode = this.GetNode(node.Prev[i]);
                var nextNode = this.GetNode(node.Next[i]);

                if (prevNode != null)
                {
                    prevNode.SetNext((byte)i, node.Next[i]);
                }
                if (nextNode != null)
                {
                    nextNode.SetPrev((byte)i, node.Prev[i]);
                }
            }

            node.Page.DeleteIndexNode(node.Position.Index);

            _snapshot.AddOrRemoveFreeIndexList(node.Page, ref index.FreeIndexPageList);
        }

        /// <summary>
        /// Delete all index nodes from a specific collection index. Scan over all PK nodes, read all nodes list and remove
        /// </summary>
        public void DropIndex(CollectionIndex index)
        {
            var slot = index.Slot;
            var pkIndex = _snapshot.CollectionPage.PK;

            foreach(var pkNode in this.FindAll(pkIndex, Query.Ascending))
            {
                var next = pkNode.NextNode;
                var last = pkNode;

                while (next != PageAddress.Empty)
                {
                    var node = this.GetNode(next);

                    if (node.Slot == slot)
                    {
                        // delete node from page (mark as dirty)
                        node.Page.DeleteIndexNode(node.Position.Index);

                        last.SetNextNode(node.NextNode);
                    }
                    else
                    {
                        last = node;
                    }

                    next = node.NextNode;
                }
            }

            // removing head/tail index nodes
            this.GetNode(index.Head).Page.DeleteIndexNode(index.Head.Index);
            this.GetNode(index.Tail).Page.DeleteIndexNode(index.Tail.Index);
        }

        #region Find

        /// <summary>
        /// Return all index nodes from an index
        /// </summary>
        public IEnumerable<IndexNode> FindAll(CollectionIndex index, int order)
        {
            var cur = order == Query.Ascending ? this.GetNode(index.Head) : this.GetNode(index.Tail);
            var counter = 0u;

            while (!cur.GetNextPrev(0, order).IsEmpty)
            {
                ENSURE(counter++ < _maxItemsCount, "Detected loop in FindAll({0})", index.Name);

                cur = this.GetNode(cur.GetNextPrev(0, order));

                // stop if node is head/tail
                if (cur.Key.IsMinValue || cur.Key.IsMaxValue) yield break;

                yield return cur;
            }
        }

        /// <summary>
        /// Find first node that index match with value .
        /// If index are unique, return unique value - if index are not unique, return first found (can start, middle or end)
        /// If not found but sibling = true and key are not found, returns next value index node (if order = Asc) or prev node (if order = Desc)
        /// </summary>
        public IndexNode Find(CollectionIndex index, BsonValue value, bool sibling, int order)
        {
            var leftNode = order == Query.Ascending ? this.GetNode(index.Head) : this.GetNode(index.Tail);
            var counter = 0u;

            for (int level = MAX_LEVEL_LENGTH - 1; level >= 0; level--)
            {
                var right = leftNode.GetNextPrev((byte)level, order);

                while (right.IsEmpty == false)
                {
                    ENSURE(counter++ < _maxItemsCount, "Detected loop in Find({0}, {1})", index.Name, value);

                    var rightNode = this.GetNode(right);

                    var diff = rightNode.Key.CompareTo(value, _collation);

                    if (diff == order && (level > 0 || !sibling)) break; // go down one level

                    if (diff == order && level == 0 && sibling)
                    {
                        // is head/tail?
                        return (rightNode.Key.IsMinValue || rightNode.Key.IsMaxValue) ? null : rightNode;
                    }

                    // if equals, return index node
                    if (diff == 0)
                    {
                        return rightNode;
                    }

                    leftNode = rightNode;
                    right = rightNode.GetNextPrev((byte)level, order);
                }
            }

            return null;
        }

        #endregion
    }
}