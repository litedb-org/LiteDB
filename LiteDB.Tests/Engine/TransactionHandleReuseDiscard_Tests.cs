using System;
using System.IO;
using System.Linq;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;
using static LiteDB.Tests.Engine.SharedWaitGuards_Tests;

namespace LiteDB.Tests.Engine
{
    /// <summary>When a reused Shared holder wrapper is discarded instead (#3083): settings drift and failures.</summary>
    [Collection(NativeFileSyncCollection.Name)]
    public class TransactionHandleReuseDiscard_Tests
    {
        public enum Drift { AddPassword, ChangePassword, RemovePassword, ChangeCollation }

        [Theory]
        [InlineData(Drift.AddPassword)]
        [InlineData(Drift.ChangePassword)]
        [InlineData(Drift.RemovePassword)]
        [InlineData(Drift.ChangeCollation)]
        public void Rebuild_changing_password_or_collation_discards_the_wrapper(Drift drift)
        {
            using var file = new TempFile();
            var before = drift == Drift.AddPassword || drift == Drift.ChangeCollation ? null : "old-secret";
            var after = drift == Drift.AddPassword || drift == Drift.ChangePassword ? "new-secret" : null;
            Seed(file, before);
            var shared = new SharedEngine(new EngineSettings { Filename = file, Password = before });
            using (var db = new LiteDatabase(shared))
            {
                using (var warm = db.BeginTransaction())
                {
                    warm.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = 20, ["name"] = "Ä" });
                    warm.Commit();
                }
                var cached = shared.CachedTransactionChild;
                Assert.NotNull(cached);
                db.Rebuild(new RebuildOptions
                {
                    Password = drift == Drift.AddPassword || drift == Drift.ChangePassword ? after : null,
                    RemovePassword = drift == Drift.RemovePassword,
                    Collation = drift == Drift.ChangeCollation ? new Collation("en-US/IgnoreCase") : null
                });
                using (var tx = db.BeginTransaction())
                {
                    var rows = tx.GetCollection("rows");
                    Assert.Equal(new[] { 1, 2 }, rows.FindAll().Select(row => row["_id"].AsInt32).OrderBy(id => id));
                    if (drift == Drift.ChangeCollation) Assert.Equal(2, rows.FindOne(Query.EQ("name", "ä"))["_id"].AsInt32);
                    rows.Insert(Row(3));
                    tx.Commit();
                }
                // The wrapper of the old configuration was discarded; the next one is cached.
                Assert.Equal(1, ReuseAccess.Discards(shared, "settings"));
                Assert.True(ReuseAccess.IsDisposed(cached));
                Assert.NotSame(cached, shared.CachedTransactionChild);
                Assert.Equal(2, shared.TransactionChildrenCreated);
            }
            Verify(file, after, 1, 2, 3);
            if (drift != Drift.ChangeCollation) return;
            using var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = after });
            Assert.Equal("en-US/IgnoreCase", cold.Collation.ToString());
            Assert.Equal(2, cold.GetCollection("rows").FindOne(Query.EQ("name", "ä"))["_id"].AsInt32);
        }

        [Fact]
        public void Holder_settings_fingerprint_compares_every_reopened_setting()
        {
            var parent = new EngineSettings { Filename = Path.GetFullPath("fingerprint.db"), Password = "a", Collation = new Collation("pt-BR/None") };
            Assert.True(parent.SnapshotForTransactionHolder().SameHolderSettings(parent.SnapshotForTransactionHolder()));
            Func<EngineSettings, EngineSettings>[] changes =
            {
                s => { s.Password = "b"; return s; },
                s => { s.Password = null; return s; },
                s => { s.Collation = new Collation("en-US/IgnoreCase"); return s; },
                s => { s.Collation = null; return s; },
                s => { s.ReadOnly = true; return s; },
                s => { s.CacheSize = 4096; return s; },
                s => { s.TransactionPageLimit = 7; return s; },
                s => { s.Upgrade = true; return s; },
                s => { s.AutoRebuild = true; return s; },
                s => { s.DurableCommits = false; return s; },
                s => { s.SharedMutexNameStrategy = SharedMutexNameStrategy.UriEscape; return s; },
                s => { s.ReadTransform = (_, value) => value; return s; },
                s => { s.SharedWriterTimeout = TimeSpan.FromSeconds(1); return s; },
                s => { s.Filename = Path.GetFullPath("other.db"); return s; },
            };
            foreach (var change in changes)
                Assert.False(parent.SnapshotForTransactionHolder().SameHolderSettings(change(parent.SnapshotForTransactionHolder())));
        }

        public enum Failure { Open, Close, WalWrite }

        [Theory]
        [InlineData(null, Failure.Open)]
        [InlineData("secret", Failure.Open)]
        [InlineData(null, Failure.Close)]
        [InlineData("secret", Failure.Close)]
        [InlineData(null, Failure.WalWrite)]
        [InlineData("secret", Failure.WalWrite)]
        public void Failure_discards_the_wrapper_keeps_the_original_error_and_committed_rows(string password, Failure failure)
        {
            using var file = new TempFile();
            Seed(file, password);
            var armed = false;
            var settings = new EngineSettings { Filename = file, Password = password };
            // A close checkpoint that fails: the handle's commit is already in the WAL.
            settings.CheckpointStage = stage =>
            {
                if (armed && stage == "before-commit-lock") { armed = false; throw new IOException("close checkpoint failed"); }
            };
            var shared = new SharedEngine(settings);
            var expected = new[] { 1 };
            using (var db = new LiteDatabase(shared))
            {
                using (var warm = db.BeginTransaction()) warm.Commit();
                var cached = shared.CachedTransactionChild;
                Assert.NotNull(cached);
                var error = new IOException("injected " + failure);
                switch (failure)
                {
                    case Failure.Open:
                        cached.SimulateOpenEngine = () => throw error;
                        Assert.Same(error, Assert.Throws<IOException>(() => db.BeginTransaction()));
                        Assert.Equal(1, ReuseAccess.Discards(shared, "error"));
                        break;
                    case Failure.Close:
                        using (var tx = db.BeginTransaction())
                        {
                            // Enough pages that this operation core's close checkpoints (CLOSE_CHECKPOINT_PAGES).
                            tx.GetCollection("bulk").Insert(Enumerable.Range(1, 80).Select(id =>
                                new BsonDocument { ["_id"] = id, ["payload"] = new string('c', 6000) }));
                            tx.GetCollection("rows").Insert(Row(2));
                            armed = true;
                            tx.Commit();
                            Assert.Equal(LiteTransactionState.Committed, tx.State);
                        }
                        Assert.False(armed, "The close checkpoint was not reached.");
                        Assert.Equal(1, ReuseAccess.Discards(shared, "close-failure"));
                        expected = new[] { 1, 2 };
                        break;
                    case Failure.WalWrite:
                        using (var tx = db.BeginTransaction())
                        {
                            tx.GetCollection("rows").Insert(Row(2));
                            var reached = false;
                            ReuseAccess.Core(tx).SimulateDiskWriteFail = page => { reached = true; throw error; };
                            Assert.Same(error, Assert.Throws<IOException>(tx.Commit));
                            Assert.True(reached);
                            Assert.Equal(LiteTransactionState.Indeterminate, tx.State);
                        }
                        Assert.Equal(1, ReuseAccess.Discards(shared, "core-failure"));
                        break;
                }
                Assert.Null(shared.CachedTransactionChild);
                Assert.True(ReuseAccess.IsDisposed(cached));
                using (var retry = db.BeginTransaction())
                {
                    Assert.Equal(expected, retry.GetCollection("rows").FindAll().Select(row => row["_id"].AsInt32).OrderBy(id => id));
                    retry.GetCollection("rows").Insert(Row(5));
                    retry.Commit();
                }
                Assert.NotNull(shared.CachedTransactionChild);
                Assert.NotSame(cached, shared.CachedTransactionChild);
            }
            Verify(file, password, expected.Concat(new[] { 5 }).ToArray());
        }
    }
}
