using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public partial class VectorIndex_Tests
    {
        [Fact]
        public void Review2_delete_with_asymmetric_saturated_components_does_not_throw()
        {
            using var db = new LiteDatabase(":memory:");
            const int componentSize = 9;
            const int componentCount = 8;
            var count = 1 + componentSize * componentCount;
            var docs = db.GetCollection<VectorDocument>("docs");
            docs.Insert(Enumerable.Range(1, count).Select(id => new VectorDocument
            {
                Id = id,
                Embedding = new[] { 1f, (float)id }
            }));
            docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2));

            AutoTransactionMethod.MakeGenericMethod(typeof(bool)).Invoke(EngineField.GetValue(db), new object[]
            {
                new Func<TransactionService, bool>(transaction =>
                {
                    var snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
                    var metadata = snapshot.CollectionPage.GetVectorIndexMetadata("embedding_idx");
                    var lookup = new DatafileLookup(new DataService(snapshot, snapshot.MaxItemsCount), false, null);
                    var nodes = new Dictionary<int, PageAddress>();
                    var pending = new Stack<PageAddress>();
                    pending.Push(metadata.Root);
                    var seen = new HashSet<PageAddress>();
                    while (pending.Count > 0)
                    {
                        var address = pending.Pop();
                        if (!seen.Add(address)) continue;
                        var node = snapshot.GetPage<VectorIndexPage>(address.PageID).GetNode(address.Index);
                        nodes.Add(lookup.Load(node.DataBlock)["_id"].AsInt32, address);
                        foreach (var neighbor in node.GetNeighbors(0)) pending.Push(neighbor);
                    }
                    nodes.Count.Should().Be(count);
                    var adjacency = nodes.ToDictionary(pair => pair.Key, _ => new List<PageAddress>());
                    for (var component = 0; component < componentCount; component++)
                    {
                        var first = 2 + component * componentSize;
                        var members = Enumerable.Range(first, componentSize).ToArray();
                        foreach (var left in members)
                        foreach (var right in members)
                            if (left != right) adjacency[left].Add(nodes[right]);
                    }
                    // The deleted center points to one endpoint in each clique, but no
                    // endpoint backlink exists: this is a legal old-reader/asymmetric graph.
                    adjacency[1].AddRange(Enumerable.Range(0, componentCount)
                        .Select(component => nodes[2 + component * componentSize]));
                    foreach (var pair in adjacency)
                        snapshot.GetPage<VectorIndexPage>(nodes[pair.Key].PageID).GetNode(nodes[pair.Key].Index)
                            .SetNeighbors(0, pair.Value);
                    metadata.Root = nodes[1];
                    snapshot.CollectionPage.IsDirty = true;
                    return true;
                })
            });
            db.Checkpoint();

            Action delete = () => docs.Delete(1).Should().BeTrue();
            delete.Should().NotThrow();
        }
    }
}

        [Fact]
        public void Review2_random_5000_mutations_counts_deferred_marker()
        {
            using var db = new LiteDatabase(":memory:");
            var docs = db.GetCollection<VectorDocument>("docs");
            docs.Insert(Enumerable.Range(1, 5000).Select(id => new VectorDocument
            {
                Id = id,
                Embedding = new[] { (float)(id % 97), (float)(id % 31), 1f, -1f }
            }));
            docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(4));
            var random = new Random(91731);
            var live = new HashSet<int>(Enumerable.Range(1, 5000));
            var deferredAt = -1;
            var deletes = 0;
            var updates = 0;
            for (var operation = 0; operation < 1000 && live.Count > 1; operation++)
            {
                var id = live.ElementAt(random.Next(live.Count));
                if ((operation & 1) == 0)
                {
                    docs.Delete(id).Should().BeTrue();
                    live.Remove(id);
                    deletes++;
                }
                else
                {
                    docs.Update(new VectorDocument
                    {
                        Id = id,
                        Embedding = new[] { (float)random.Next(0, 10000), 3f, 2f, -2f }
                    }).Should().BeTrue();
                    updates++;
                }
                var deferred = InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
                    snapshot.CollectionPage.GetVectorIndexes().Single().Index.VectorRepairDeferred);
                if (deferred)
                {
                    deferredAt = operation + 1;
                    break;
                }
            }
            Console.WriteLine($"random_mutations live={live.Count} deletes={deletes} updates={updates} deferred_at={deferredAt}");
            deferredAt.Should().Be(-1, "ordinary random mutations should not exhaust the 1024-node repair budget");
        }
