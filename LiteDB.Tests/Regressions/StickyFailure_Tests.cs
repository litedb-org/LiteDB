#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Decision 6 of docs/decisions/durability-policy.md (a real write or sync failure is sticky: it is
    /// recorded, the engine reopens read-only on its next call, reads keep working, and every later
    /// write throws with the record). Refusals before anything was written (the data file cannot sync,
    /// implementation note 6) are no failure.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public partial class StickyFailure_Tests
    {
        /// <summary>
        /// Implementation note 6: a retiring checkpoint whose promotion is refused before it wrote
        /// anything (the data file cannot sync) is no failure. When an automatic checkpoint after a
        /// commit hit it, the refusal was recorded and the engine went read-only; recorded no longer, it
        /// must not throw from that commit either (decision 5). Writes stay durable in the WAL.
        /// </summary>
        [Fact]
        public void Automatic_checkpoint_refused_before_it_wrote_is_neither_thrown_nor_recorded()
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
                setup.GetCollection("rows").Insert(Enumerable.Range(1, 64).Select(id => MvccRetirementScenario.Document(id, 0)));
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                using var power = new FilePowerLossModel(file.Filename);
                using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                for (var value = 1; value <= 5; value++) Update(db, value, 64);
                Exception thrown = null;
                using (var reader = engine.Query("rows", new Query()))
                {
                    reader.Read().Should().BeTrue();
                    var thread = new Thread(() => thrown = Record.Exception(() =>
                    {
                        for (var value = 6; value <= 9; value++) Update(db, value, 64);
                        var data = SyncPowerLossModel.ReadShared(file.Filename);
                        power.DataFailsFromSync = power.DataSyncs + 2; // the retirement proof's data sync, then the promotion's
                        db.CheckpointSize = 1; // this pragma's commit runs a retiring checkpoint: the reader keeps the WAL
                        power.DataSyncs.Should().BeGreaterOrEqualTo(power.DataFailsFromSync, "the promotion tried its data sync");
                        SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data, "the refused promotion wrote nothing");
                    }));
                    thread.Start();
                    thread.Join();
                }
                thrown.Should().BeNull("the commit succeeded, and its checkpoint's refusal is no failure");
                Info(db)["writeFailure"].IsNull.Should().BeTrue();
                Info(db)["readOnly"].AsBoolean.Should().BeFalse();
                Update(db, 10, 64);
                Info(db)["durableLogFlush"].AsBoolean.Should().BeTrue();
                SyncPowerLossModel.AssertRows(db, 64, 10);
                power.AfterPowerLoss(x => SyncPowerLossModel.AssertRows(x, 64, 10));
            }
            finally { File.Delete(logName); }
        }

        private static void Update(LiteDatabase db, int value, int rows) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, rows).Select(id => MvccRetirementScenario.Document(id, value)));

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();
    }
}
#endif
