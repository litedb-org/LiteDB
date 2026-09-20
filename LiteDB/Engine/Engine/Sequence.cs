using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Get lastest value from a _id collection and plus 1 - use _sequence cache
        /// </summary>
        private BsonValue GetSequence(Snapshot snapshot, BsonAutoId autoId)
        {
            var next = _sequences.AddOrUpdate(snapshot.CollectionName, (s) =>
            {
                var lastId = this.GetLastId(snapshot);

                // emtpy collection, return 1
                if (lastId.IsMinValue) return 1;

                // if lastId is not number, throw exception
                if (!lastId.IsNumber)
                {
                    throw new LiteException(0, $"It's not possible use AutoId={autoId} because '{snapshot.CollectionName}' collection contains not only numbers in _id index ({lastId}).");
                }

                // return nextId
                return lastId.AsInt64 + 1;
            },
            (s, value) =>
            {
                // update last value
                return value + 1;
            });

            return autoId == BsonAutoId.Int32 ?
                new BsonValue((int)next) :
                new BsonValue(next);
        }

        /// <summary>
        /// Update sequence number with new _id passed by user, IF this number are higher than current last _id
        /// At this point, newId.Type is Number
        /// </summary>
        private void SetSequence(Snapshot snapshot, BsonValue newId)
        {
            _sequences.AddOrUpdate(snapshot.CollectionName, (s) =>
            {
                var lastId = this.GetLastId(snapshot);

                // create new collection based with max value between last _id index key or new passed _id
                if (lastId.IsNumber)
                {
                    return Math.Max(lastId.AsInt64, newId.AsInt64);
                }
                else
                {
                    // if collection last _id is not an number (is empty collection or contains another data type _id)
                    // use newId
                    return newId.AsInt64;
                }

            }, (s, value) =>
            {
                // return max value between current sequence value vs new inserted value
                return Math.Max(value, newId.AsInt64);
            });
        }

        /// <summary>
        /// Get last _id index key from collection. Returns MinValue if collection are empty
        /// </summary>
        private BsonValue GetLastId(Snapshot snapshot)
        {
            var pk = snapshot.CollectionPage.PK;

            // get tail page and previous page
            var tailPage = snapshot.GetPage<IndexPage>(pk.Tail.PageID);
            var node = tailPage.GetIndexNode(pk.Tail.Index);
            var prevNode = node.Prev[0];

            if (prevNode == pk.Head)
            {
                return BsonValue.MinValue;
            }
            else
            {
                var lastPage = prevNode.PageID == tailPage.PageID ? tailPage : snapshot.GetPage<IndexPage>(prevNode.PageID);
                var lastNode = lastPage.GetIndexNode(prevNode.Index);

                var lastKey = lastNode.Key;

                return lastKey;
            }
        }
    }
}