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

            InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
            {
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
                    snapshot.GetPage<VectorIndexPage>(nodes[pair.Key].PageID)
                        .GetNode(nodes[pair.Key].Index).SetNeighbors(0, pair.Value);
                metadata.Root = nodes[1];
                snapshot.CollectionPage.IsDirty = true;
                return true;
            }).Should().BeTrue();
            db.Checkpoint();

            Action delete = () => docs.Delete(1).Should().BeTrue();
            delete.Should().NotThrow();
        }
    }
}
