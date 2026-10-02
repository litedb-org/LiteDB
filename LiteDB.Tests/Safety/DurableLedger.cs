using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Tests.Safety
{
    /// <summary>
    /// Durability oracle: what a cold reopen (after close, or after the writing process exited)
    /// must show. Acknowledged effects are present with their acknowledged content; known-aborted
    /// effects are absent (or still show the value from before the aborted write). Outcome-unknown
    /// writes are simply not recorded. <see cref="ExpectExactly"/> pins a whole collection when
    /// the harness models every document of it.
    /// </summary>
    internal sealed class DurableLedger
    {
        private readonly Dictionary<(string Collection, BsonValue Id), BsonDocument> _acknowledged =
            new Dictionary<(string, BsonValue), BsonDocument>();
        private readonly Dictionary<(string Collection, BsonValue Id), BsonDocument> _aborted =
            new Dictionary<(string, BsonValue), BsonDocument>();
        private readonly Dictionary<string, BsonDocument[]> _exact = new Dictionary<string, BsonDocument[]>(StringComparer.Ordinal);

        public int Count => _acknowledged.Count + _aborted.Count + _exact.Values.Sum(documents => documents.Length);

        /// <summary>A committed write (or, with null, a committed delete) of one document.</summary>
        public void Acknowledge(string collection, BsonValue id, BsonDocument document)
        {
            _acknowledged[(collection, id)] = document == null ? null : Clone(document);
            _aborted.Remove((collection, id));
        }

        /// <summary>A write known to have rolled back: the document keeps <paramref name="before"/> (null: absent).</summary>
        public void Abort(string collection, BsonValue id, BsonDocument before)
        {
            if (_acknowledged.ContainsKey((collection, id))) return;
            _aborted[(collection, id)] = before == null ? null : Clone(before);
        }

        /// <summary>The collection holds exactly these documents (ordered by _id).</summary>
        public void ExpectExactly(string collection, IEnumerable<BsonDocument> documents) =>
            _exact[collection] = documents.Select(Clone).OrderBy(document => document["_id"]).ToArray();

        /// <summary>Every violation found in a cold-opened database; empty when durable.</summary>
        public IReadOnlyList<string> Verify(ILiteDatabase database)
        {
            var violations = new List<string>();
            foreach (var pair in _acknowledged)
            {
                var actual = database.GetCollection(pair.Key.Collection).FindById(pair.Key.Id);
                if (!Same(actual, pair.Value))
                    violations.Add($"ACKNOWLEDGED_LOST: {pair.Key.Collection}/{pair.Key.Id} expected " +
                        $"{Show(pair.Value)}, found {Show(actual)}");
            }
            foreach (var pair in _aborted)
            {
                var actual = database.GetCollection(pair.Key.Collection).FindById(pair.Key.Id);
                if (!Same(actual, pair.Value))
                    violations.Add($"ABORTED_VISIBLE: {pair.Key.Collection}/{pair.Key.Id} expected " +
                        $"{Show(pair.Value)}, found {Show(actual)}");
            }
            foreach (var pair in _exact)
            {
                var actual = database.GetCollection(pair.Key).Query().OrderBy("_id").ToArray();
                if (actual.Length != pair.Value.Length || actual.Zip(pair.Value, Same).Any(equal => !equal))
                    violations.Add($"ACKNOWLEDGED_LOST: collection {pair.Key} differs from its acknowledged state " +
                        $"({actual.Length} documents, expected {pair.Value.Length})");
            }
            return violations;
        }

        private static bool Same(BsonDocument left, BsonDocument right) =>
            left == null || right == null ? left == null && right == null
                : BsonSerializer.Serialize(left).SequenceEqual(BsonSerializer.Serialize(right));

        private static string Show(BsonDocument document) => document == null ? "absent" : JsonSerializer.Serialize(document);

        private static BsonDocument Clone(BsonDocument document) =>
            BsonSerializer.Deserialize(BsonSerializer.Serialize(document));
    }
}
