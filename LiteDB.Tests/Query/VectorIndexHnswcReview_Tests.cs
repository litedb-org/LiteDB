using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public partial class VectorIndex_Tests
    {
        [Fact]
        public void Review2_hnswc_asymmetric_fixture_endpoint_delete_never_frees_a_referenced_node()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, AsymmetricFixture());
            PageAddress removed = PageAddress.Empty;
            using (var db = new LiteDatabase(file.Filename))
            {
                removed = InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
                {
                    var root = snapshot.GetPage<VectorIndexPage>(metadata.Root.PageID).GetNode(metadata.Root.Index);
                    var endpoint = root.GetNeighbors(0)[0];
                    var node = snapshot.GetPage<VectorIndexPage>(endpoint.PageID).GetNode(endpoint.Index);
                    new DatafileLookup(new DataService(snapshot, snapshot.MaxItemsCount), false, null)
                        .Load(node.DataBlock)["_id"].AsInt32.Should().Be(2);
                    return endpoint;
                });
                db.GetCollection<VectorDocument>("docs").Delete(2).Should().BeTrue();
                db.GetCollection<VectorDocument>("docs").Count().Should().Be(72);
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
                {
                    var root = snapshot.GetPage<VectorIndexPage>(metadata.Root.PageID).GetNode(metadata.Root.Index);
                    var referenced = root.GetNeighbors(0).Contains(removed);
                    var allocated = snapshot.GetPage<VectorIndexPage>(removed.PageID).GetNodes().Any(n => n.Position == removed);
                    Console.WriteLine($"endpoint_delete deferred={RepairDeferred(db)} referenced={referenced} allocated={allocated}");
                    (referenced && !allocated).Should().BeFalse("successful deletion must never leave a persisted dangling vector edge");
                    return true;
                }).Should().BeTrue();
            }
        }

    }
}
