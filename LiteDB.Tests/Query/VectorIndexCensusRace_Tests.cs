using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Engine;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public partial class VectorIndex_Tests
    {
        // The write census reads foreign pages at their newest committed WAL frame,
        // which the writer's pin does not protect. Supersede that frame and let a
        // partial checkpoint reclaim it between position choice and the read.
        [Fact]
        public void Review2_hnswe_census_latest_frame_survives_supersede_and_checkpoint()
        {
            using var file = new TempFile();
            var reached = 0;
            Exception peerError = null;
            using (var db = new LiteDatabase(file.Filename))
            {
                BuildBoundedRepairGraph(db, 65, arms: false);
                db.CheckpointSize = 0;
                var engine = (LiteEngine)EngineField.GetValue(db);
                db.BeginTrans().Should().BeTrue();
                // Pin the writer's read version before the foreign commit.
                CensusSnapshot(db, LockMode.Write, snapshot => snapshot.ReadVersion);
                Task.Run(() => db.GetCollection("foreign").Insert(new BsonDocument
                    { ["_id"] = 1, ["payload"] = new string('x', 40000) })).GetAwaiter().GetResult();
                // Newest frames of the foreign data pages: committed after the pin.
                var positions = Task.Run(() =>
                {
                    var walIndex = engine.GetWalIndex();
                    return new HashSet<long>(db.Execute("SELECT $ FROM $dump WHERE $.collection = 'foreign' AND $.pageType = 'Data'")
                        .ToArray().Select(x => walIndex.GetPageIndex((uint)x["pageID"].AsInt32, int.MaxValue, out _))
                        .Where(x => x != long.MaxValue));
                }).GetAwaiter().GetResult();
                positions.Should().NotBeEmpty();
                var threadID = Environment.CurrentManagedThreadId;
                engine.BeforePageRead = (position, origin) =>
                {
                    if (Environment.CurrentManagedThreadId != threadID || origin != FileOrigin.Log ||
                        !positions.Contains(position) || Interlocked.Exchange(ref reached, 1) != 0) return;
                    try
                    {
                        Task.Run(() =>
                        {
                            db.GetCollection("foreign").Update(new BsonDocument
                                { ["_id"] = 1, ["payload"] = new string('y', 40000) }).Should().BeTrue();
                            db.Checkpoint();
                        }).GetAwaiter().GetResult();
                    }
                    catch (Exception ex) { peerError = ex; }
                };
                Exception deleteError = null;
                try { db.GetCollection<VectorDocument>("docs").Delete(2).Should().BeTrue(); }
                catch (Exception ex) { deleteError = ex; }
                finally { engine.BeforePageRead = null; }
                reached.Should().Be(1, "the census must read a foreign frame chosen after the pin");
                peerError.Should().BeNull();
                deleteError.Should().BeNull("a concurrent checkpoint must not reclaim the frame the census chose");
                db.Commit().Should().BeTrue();
                db.GetCollection("foreign").FindById(1)["payload"].AsString.Should().Be(new string('y', 40000));
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection<VectorDocument>("docs").Count().Should().Be(64);
                AssertAllocatedVectorLinks(db, 64);
                db.GetCollection("foreign").FindById(1)["payload"].AsString.Should().Be(new string('y', 40000));
            }
        }
    }
}
