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

        [Theory]
        [InlineData("drop", false)]
        [InlineData("drop", true)]
        [InlineData("repair", false)]
        [InlineData("repair", true)]
        public void Census_latest_read_pin_survives_checkpoint_and_is_released_on_success_or_failure(string operation, bool failRead)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                BuildBoundedRepairGraph(db, 65, arms: false);
                db.CheckpointSize = 0;
                db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "untouched" });
                if (operation == "repair") SetCensusDeferred(db);
                db.Checkpoint();
                var engine = (LiteEngine)EngineField.GetValue(db);
                var wal = engine.GetWalIndex();
                db.BeginTrans().Should().BeTrue();
                CensusSnapshot(db, LockMode.Write, snapshot => snapshot.ReadVersion);
                Task.Run(() => InsertCensusForeign(db, "foreign")).GetAwaiter().GetResult();
                var positions = Task.Run(() => new HashSet<long>(CensusCollectionPages(db, "foreign")
                    .Select(id => wal.GetPageIndex(id, int.MaxValue, out _)).Where(x => x != long.MaxValue)))
                    .GetAwaiter().GetResult();
                positions.Should().NotBeEmpty();
                var pins = wal.SnapshotCount;
                var threadID = Environment.CurrentManagedThreadId;
                var reached = 0;
                engine.BeforePageRead = (position, origin) =>
                {
                    if (Environment.CurrentManagedThreadId != threadID || origin != FileOrigin.Log ||
                        !positions.Contains(position) || Interlocked.Exchange(ref reached, 1) != 0) return;
                    wal.SnapshotCount.Should().Be(pins + 1, "the selected page needs its own live floor");
                    var selectedVersion = wal.CurrentReadVersion;
                    Task.Run(() =>
                    {
                        db.GetCollection("foreign").Update(new BsonDocument
                            { ["_id"] = 1, ["payload"] = new string('y', 40000) }).Should().BeTrue();
                        db.Checkpoint();
                        // Verify the checkpoint ran without dropping the selected
                        // version, rather than simply postponing it until read end.
                        wal.SnapshotPositions(selectedVersion).Should().Contain(position);
                    }).GetAwaiter().GetResult();
                    if (failRead) throw new System.IO.IOException("census read interrupted");
                };
                Exception error = null;
                try
                {
                    var docs = db.GetCollection<VectorDocument>("docs");
                    if (operation == "drop") docs.DropIndex("embedding_idx").Should().BeTrue();
                    else docs.Delete(2).Should().BeTrue();
                }
                catch (Exception ex) { error = ex; }
                finally { engine.BeforePageRead = null; }
                reached.Should().Be(1);
                // A failed auto operation rolls back the explicit transaction too.
                wal.SnapshotCount.Should().Be(failRead ? 0 : pins);
                if (failRead) error.Should().BeOfType<System.IO.IOException>().Which.Message.Should().Be("census read interrupted");
                else
                {
                    error.Should().BeNull();
                    db.Commit().Should().BeTrue();
                }
                // An injected I/O error intentionally stops the engine. The pin
                // must still be released, and cold recovery must roll back here.
                if (!failRead)
                {
                    AssertCensusRaceOutcome(db, operation, false);
                    db.Checkpoint();
                }
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                AssertCensusRaceOutcome(db, operation, failRead);
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename)) AssertCensusRaceOutcome(db, operation, failRead);
        }

        private static void AssertCensusRaceOutcome(LiteDatabase db, string operation, bool failed)
        {
            var count = operation == "repair" && !failed ? 64 : 65;
            db.GetCollection<VectorDocument>("docs").Count().Should().Be(count);
            if (operation == "drop" && !failed) AllocatedVectorPages(db, "docs").Should().Be(0);
            else
            {
                AssertAllocatedVectorLinks(db, count);
                RepairDeferred(db).Should().Be(operation == "repair" && failed);
            }
            db.GetCollection("foreign").FindById(1)["payload"].AsString.Should().Be(new string('y', 40000));
            db.GetCollection("cold").FindById(1)["value"].AsString.Should().Be("untouched");
        }
    }
}
