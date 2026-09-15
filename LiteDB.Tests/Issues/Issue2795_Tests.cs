using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using FluentAssertions;
using FluentAssertions.Execution;

using LiteDB.Engine;

using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2795_Tests
    {
        private const string Collection = "issue2795";
        private const int DocumentCount = 2063;
        private const int Offset = 2000;
        private const int PageSize = 7;
        private const int PayloadLength = 4096;
        private const uint MaximumItems = 1_000_000;

        [Fact]
        public void Offset_skips_index_nodes_without_loading_skipped_documents()
        {
            using var engine = new LiteEngine();

            var insertionOrder = Enumerable.Range(1, DocumentCount)
                .OrderBy(id => (id * 1543) % DocumentCount)
                .ToArray();

            engine.Insert(Collection, insertionOrder.Select(CreateDocument), BsonAutoId.Int32);
            engine.EnsureIndex(Collection, "sequence_idx", "$.sequence", false);

            var plainFirst = ExecuteMeasured(engine, ordered: false, offset: 0);
            var plainLate = ExecuteMeasured(engine, ordered: false, offset: Offset);
            var orderedFirst = ExecuteMeasured(engine, ordered: true, offset: 0);
            var orderedLate = ExecuteMeasured(engine, ordered: true, offset: Offset);
            var rangedDescending = ExecuteMeasured(
                engine, ordered: true, offset: Offset, descending: true, indexedRange: true);

            using var assertions = new AssertionScope();
            AssertPage(plainFirst, ordered: false, offset: 0);
            AssertPage(plainLate, ordered: false, offset: Offset);
            AssertPage(orderedFirst, ordered: true, offset: 0);
            AssertPage(orderedLate, ordered: true, offset: Offset);
            AssertPage(rangedDescending, ordered: true, offset: Offset, descending: true);
            rangedDescending.HasResidualFilters.Should().BeFalse(
                "the indexed range predicate must be fully consumed before offset");
            rangedDescending.IsFullIndexScan.Should().BeFalse(
                "the range case must not silently fall back to the full-index route");
        }

        private static QueryOutcome ExecuteMeasured(
            LiteEngine engine,
            bool ordered,
            int offset,
            bool descending = false,
            bool indexedRange = false)
        {
            var query = new Query
            {
                Select = BsonExpression.Root,
                Offset = offset,
                Limit = PageSize
            };

            if (indexedRange)
            {
                query.Where.Add(BsonExpression.Create("$.sequence >= 1"));
            }

            if (ordered)
            {
                query.OrderBy.Add(new QueryOrder(
                    BsonExpression.Create("$.sequence"),
                    descending ? Query.Descending : Query.Ascending));
            }

            var pragmas = new EnginePragmas(null);
            var monitor = engine.GetMonitor();
            var transaction = monitor.GetTransaction(true, true, out var isNew);
            using var tempDisk = new SortDisk(
                new StreamFactory(new MemoryStream(), null, true),
                Constants.PAGE_SIZE,
                pragmas);

            try
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Read, Collection, false);
                var plan = new QueryOptimization(snapshot, query, null, pragmas.Collation).ProcessQuery();
                var innerLookup = plan.GetLookup(snapshot, pragmas, MaximumItems);
                var lookup = new CountingLookup(innerLookup);
                var traversedKeys = new List<int>();
                var indexer = new IndexService(snapshot, pragmas.Collation, MaximumItems);
                var nodes = RecordNodes(plan.Index.Run(snapshot.CollectionPage, indexer), traversedKeys);
                var pipe = new QueryPipe(transaction, lookup, tempDisk, pragmas, MaximumItems);
                var rows = pipe.Pipe(nodes, plan).ToArray();

                return new QueryOutcome(
                    rows,
                    traversedKeys,
                    lookup.LoadedDocumentIds,
                    lookup.MaterializedPayloadCharacters,
                    plan.IndexExpression,
                    plan.OrderBy == null,
                    plan.IsIndexKeyOnly,
                    plan.Filters.Count > 0,
                    plan.Index is IndexAll);
            }
            finally
            {
                if (isNew)
                {
                    monitor.ReleaseTransaction(transaction);
                }
            }
        }

        private static IEnumerable<IndexNode> RecordNodes(
            IEnumerable<IndexNode> nodes,
            ICollection<int> traversedKeys)
        {
            foreach (var node in nodes)
            {
                traversedKeys.Add(node.Key.AsInt32);
                yield return node;
            }
        }

        private static void AssertPage(
            QueryOutcome outcome,
            bool ordered,
            int offset,
            bool descending = false)
        {
            var expectedKeys = descending
                ? Enumerable.Range(0, PageSize).Select(index => DocumentCount - offset - index).ToArray()
                : Enumerable.Range(offset + 1, PageSize).ToArray();
            var expectedIds = ordered
                ? expectedKeys.Select(sequence => DocumentCount - sequence + 1).ToArray()
                : expectedKeys;
            var expectedSignatures = expectedIds.Select(ExpectedSignature).ToArray();

            outcome.IndexExpression.Should().Be(ordered ? "$.sequence" : "$._id");
            outcome.IndexConsumedOrdering.Should().BeTrue("the selected index must own the requested order");
            outcome.IsIndexKeyOnly.Should().BeFalse("the full payload was requested");
            outcome.Rows.Select(Signature).Should().Equal(expectedSignatures,
                "offset pagination must preserve the independently calculated page");

            outcome.TraversedKeys.Count.Should().BeInRange(PageSize, offset + PageSize,
                "the index may seek directly but must not enumerate beyond the requested page");
            outcome.TraversedKeys.Skip(outcome.TraversedKeys.Count - PageSize).Should().Equal(expectedKeys);

            outcome.LoadedDocumentIds.Should().Equal(expectedIds,
                "only documents returned to the caller may be deserialized");
            outcome.MaterializedPayloadCharacters.Should().Be(PageSize * PayloadLength,
                "work must depend on the page size, not the offset");
        }

        private static BsonDocument CreateDocument(int id)
        {
            var sequence = DocumentCount - id + 1;

            return new BsonDocument
            {
                ["_id"] = id,
                ["sequence"] = sequence,
                ["proof"] = Proof(id, sequence),
                ["payload"] = CreatePayload(id)
            };
        }

        private static string ExpectedSignature(int id)
        {
            var sequence = DocumentCount - id + 1;
            return $"{id}|{sequence}|{Proof(id, sequence)}|{PayloadLength}|{ExpectedPayloadHash(id):x8}";
        }

        private static string Signature(BsonDocument document)
        {
            var payload = document["payload"].AsString;
            return $"{document["_id"].AsInt32}|{document["sequence"].AsInt32}|" +
                   $"{document["proof"].AsInt32}|{payload.Length}|{ActualPayloadHash(payload):x8}";
        }

        private static int Proof(int id, int sequence)
        {
            return unchecked((id * 7919) ^ (sequence * 104729) ^ 0x2795);
        }

        private static string CreatePayload(int id)
        {
            var characters = new char[PayloadLength];

            for (var index = 0; index < characters.Length; index++)
            {
                characters[index] = ExpectedPayloadCharacter(id, index);
            }

            return new string(characters);
        }

        private static uint ExpectedPayloadHash(int id)
        {
            var hash = 2166136261u;

            for (var index = 0; index < PayloadLength; index++)
            {
                hash = unchecked((hash ^ ExpectedPayloadCharacter(id, index)) * 16777619u);
            }

            return hash;
        }

        private static uint ActualPayloadHash(string payload)
        {
            var hash = 2166136261u;

            foreach (var character in payload)
            {
                hash = unchecked((hash ^ character) * 16777619u);
            }

            return hash;
        }

        private static char ExpectedPayloadCharacter(int id, int index)
        {
            return (char)('A' + ((id * 17) + (index * 13)) % 23);
        }

        private sealed class CountingLookup : IDocumentLookup
        {
            private readonly IDocumentLookup _inner;

            public CountingLookup(IDocumentLookup inner)
            {
                _inner = inner;
            }

            public List<int> LoadedDocumentIds { get; } = new List<int>();
            public int MaterializedPayloadCharacters { get; private set; }

            public BsonDocument Load(IndexNode node)
            {
                return Record(_inner.Load(node));
            }

            public BsonDocument Load(PageAddress rawId)
            {
                return Record(_inner.Load(rawId));
            }

            private BsonDocument Record(BsonDocument document)
            {
                LoadedDocumentIds.Add(document["_id"].AsInt32);
                MaterializedPayloadCharacters += document["payload"].AsString.Length;
                return document;
            }
        }

        private sealed class QueryOutcome
        {
            public QueryOutcome(
                BsonDocument[] rows,
                List<int> traversedKeys,
                List<int> loadedDocumentIds,
                int materializedPayloadCharacters,
                string indexExpression,
                bool indexConsumedOrdering,
                bool isIndexKeyOnly,
                bool hasResidualFilters,
                bool isFullIndexScan)
            {
                Rows = rows;
                TraversedKeys = traversedKeys;
                LoadedDocumentIds = loadedDocumentIds;
                MaterializedPayloadCharacters = materializedPayloadCharacters;
                IndexExpression = indexExpression;
                IndexConsumedOrdering = indexConsumedOrdering;
                IsIndexKeyOnly = isIndexKeyOnly;
                HasResidualFilters = hasResidualFilters;
                IsFullIndexScan = isFullIndexScan;
            }

            public BsonDocument[] Rows { get; }
            public List<int> TraversedKeys { get; }
            public List<int> LoadedDocumentIds { get; }
            public int MaterializedPayloadCharacters { get; }
            public string IndexExpression { get; }
            public bool IndexConsumedOrdering { get; }
            public bool IsIndexKeyOnly { get; }
            public bool HasResidualFilters { get; }
            public bool IsFullIndexScan { get; }
        }
    }
}
