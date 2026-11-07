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
            var counter = _sequences.GetOrAdd(snapshot.CollectionName, (s) =>
            {
                var lastId = this.GetLastId(snapshot);
                if (lastId.IsMinValue) return new SequenceCounter(0);
                if (!lastId.IsNumber)
                {
                    throw new LiteException(0, $"It's not possible use AutoId={autoId} because '{snapshot.CollectionName}' collection contains not only numbers in _id index ({lastId}).");
                }
                return new SequenceCounter(lastId.AsInt64);
            });
            var next = counter.Next();

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
            var value = newId.AsInt64;
            if (!_sequences.TryGetValue(snapshot.CollectionName, out var counter))
            {
                var lastId = this.GetLastId(snapshot);
                var initial = lastId.IsNumber ? Math.Max(lastId.AsInt64, value) : value;
                counter = _sequences.GetOrAdd(snapshot.CollectionName, new SequenceCounter(initial));
            }
            counter.SetMax(value);
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