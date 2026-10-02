using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LiteDB.Engine;
using LiteDB.Utils;
using Xunit;

namespace LiteDB.Tests.Safety.Tests
{
    /// <summary>
    /// Minimal reproductions of the teardown sweep's known findings on dev 7b71bc4d (registered in
    /// <see cref="TeardownKnownFindings"/>; docs/teardown-sweep.md, "Known findings"). Each test passes
    /// while the defect exists. When a fix lands, the sweep reports the finding as "no longer
    /// reproduces": remove its registration and turn its test here into a passing contract check.
    /// All three contradict docs/rules/storage-ownership.md, "Buffers and cleanup": every stream needs a
    /// release path for exceptions and repeated disposal, and cleanup failures must not skip cleanup.
    /// </summary>
    public class TeardownKnownFinding_Tests
    {
        /// <summary>
        /// Known finding litedatabase-dispose-skips-engine-after-checkpoint-restore-failure, natural trigger,
        /// no fault injection: a database over a caller's FileStream forces CHECKPOINT 1 and restores the
        /// original value in Dispose(bool) before <c>_engine.Dispose()</c>, without try/finally. Disposing
        /// it while its thread still has an open explicit transaction (a <c>using</c> block that unwinds
        /// mid-transaction) makes the restore throw INVALID_TRANSACTION_STATE ("already contains an open transaction"): Dispose throws that instead
        /// of returning, and the engine with its transaction, streams and lock service stays open. The
        /// same sequence on a file-path database (no override) disposes cleanly.
        /// </summary>
        [Fact]
        public void Known_finding_stream_database_dispose_with_an_open_transaction_throws_and_leaves_its_engine_open()
        {
            using (var file = new TempFile())
            using (var stream = new FileStream(file.Filename, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                var db = new LiteDatabase(stream);
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                Assert.True(db.BeginTrans());
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });

                var error = Record.Exception(() => db.Dispose());

                var lite = Assert.IsType<LiteException>(error);
                Assert.Equal(LiteException.INVALID_TRANSACTION_STATE, lite.ErrorCode);
                Assert.Contains("engine: the Direct engine is not disposed", ConnectionCleanProbe.Evaluate(db).Violations);

                // Release what the failed Dispose left: end the transaction, then Dispose again.
                db.Rollback();
                db.Dispose();
                Assert.Empty(ConnectionCleanProbe.Evaluate(db).Violations);
            }

            using (var file = new TempFile())
            {
                var control = new LiteDatabase(file.Filename);
                control.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                Assert.True(control.BeginTrans());
                control.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                control.Dispose();
                Assert.Empty(ConnectionCleanProbe.Evaluate(control).Violations);
            }
        }

        /// <summary>
        /// Known finding sortdisk-dispose-skips-scratch-delete, controlled fault (evidence class 1):
        /// SortDisk.Dispose runs <c>_pool.Dispose()</c> then <c>_factory.Delete()</c> without try/finally.
        /// When closing the scratch streams fails, the -tmp file holding the last spilled sort's keys is
        /// never deleted; LiteEngine.Close collects the error, LiteEngine.Dispose discards it, and nothing
        /// deletes the file later. One fail-inside fault at that step reproduces it.
        /// </summary>
        [Fact]
        public void Known_finding_sort_scratch_survives_the_close_when_closing_its_streams_fails()
        {
            using (var file = new TempFile())
            {
                var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
                var db = new LiteDatabase(engine, disposeOnClose: false);
                SpillSort(db);
                var scratch = FileHelper.GetTempFile(file.Filename);
                Assert.True(File.Exists(scratch), "the sort did not spill to the scratch file");

                var scenario = Armed("SortDisk.Dispose.pool", TeardownStepSite.After);
                using (TeardownSteps.Begin(scenario))
                {
                    scenario.OpenWindow();
                    engine.Dispose();
                }

                Assert.NotNull(scenario.Fired);
                Assert.True(File.Exists(scratch), "the scratch file was deleted after all");
                File.Delete(scratch);
            }
        }

#if NET8_0_OR_GREATER // mapped coordination, the files the skipped step closes, exists on .NET 8+ only
        /// <summary>
        /// Known finding shared-dispose-aborts-remaining-cleanup, controlled fault (evidence class 1):
        /// SharedEngine.Dispose sets <c>_disposed</c> first and then runs its cleanup steps unguarded. A
        /// failure while closing the reader registry propagates out of Dispose and skips the coordination
        /// disposal after it; a second Dispose returns at once, so the coordination files stay open until
        /// the process exits. The shape dates from #3003 (3b9e579f1).
        /// </summary>
        [Fact]
        public void Known_finding_shared_dispose_failure_skips_the_remaining_cleanup_for_good()
        {
            using (var file = new TempFile())
            {
                var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared });
                // Repeated operations make the connection create its coordination authority (one-shot ones do not).
                for (var id = 1; id <= 3; id++)
                {
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = id });
                    Assert.Equal(id, db.GetCollection("rows").Count());
                }
                var engine = (SharedEngine)ConnectionCleanProbe.EngineOf(db);
                var field = typeof(SharedEngine).GetField("_coordination", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.True(field?.GetValue(engine) != null, "no mapped coordination: " + engine.CoordinationFallbackReason);

                var scenario = Armed("SharedEngine.Dispose.readers", TeardownStepSite.After);
                Exception error;
                using (TeardownSteps.Begin(scenario))
                {
                    scenario.OpenWindow();
                    error = Record.Exception(() => db.Dispose());
                }

                Assert.NotNull(scenario.Fired);
                Assert.Same(scenario.Fired, error);
                db.Dispose(); // a retry is a no-op: the connection is marked disposed
                Assert.NotNull(field.GetValue(engine));
                var open = QuiescentProbe.Evaluate(file.Filename).Violations.Where(item => item.StartsWith("handles", StringComparison.Ordinal)).ToArray();
                Assert.Contains(open, item => item.Contains("-shared-live") || item.Contains("-shared-state"));

                // Release what the failed Dispose skipped, so this test leaves no open files behind.
                typeof(SharedEngine).GetMethod("DisposeCoordination", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(engine, null);
            }
        }
#endif

        private static TeardownScenario Armed(string step, TeardownStepSite site)
        {
            var scenario = new TeardownScenario();
            scenario.Arm(step, site, 1, () => new IOException("known-finding reproduction fault at " + step));
            return scenario;
        }

        /// <summary>A sorted query whose keys exceed one sort container, left open so its scratch stays live.</summary>
        private static IEnumerator<BsonDocument> SpillSort(LiteDatabase db)
        {
            db.GetCollection("big").InsertBulk(Enumerable.Range(1, 1100).Select(id => new BsonDocument
            {
                ["_id"] = id, ["key"] = (id * 7919 % 1100).ToString("D6") + new string('k', 760)
            }));
            var reader = db.GetCollection("big").Query().OrderBy("key").ToEnumerable().GetEnumerator();
            Assert.True(reader.MoveNext());
            return reader;
        }
    }
}
