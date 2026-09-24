#if !NETFRAMEWORK
#pragma warning disable LITEDB_EXPERIMENTAL_COORDINATOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Snapshot reads without IPC in the experimental coordinator: the status-page fast
    /// path, the lock-free handshake that opens new snapshots, and incremental refresh.
    /// Every engine instance in one process is a separate participant, as in Coordinator_Tests.
    /// </summary>
    public class CoordinatorSnapshotReads_Tests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

        [Fact]
        public void Unchanged_reads_reuse_the_cached_snapshot_without_ipc()
        {
            using var file = new TempFile();
            using var host = new CoordinatedEngine(file.Filename);
            using var client = new CoordinatedEngine(file.Filename);
            using var hostDb = new LiteDatabase(host, disposeOnClose: false);
            using var clientDb = new LiteDatabase(client, disposeOnClose: false);
            hostDb.GetCollection("docs").Insert(Enumerable.Range(1, 20).Select(i => new BsonDocument { ["_id"] = i }));
            var docs = clientDb.GetCollection("docs");
            (docs.FindById(1) != null).Should().BeTrue();
            var reads = client.ClientForTests;
            var (hits, grants, ipc, opens) = (reads.PageHits, reads.GrantOpens, reads.IpcReads, reads.SnapshotOpens);
            reads.HandshakeOpens.Should().Be(1, "the first snapshot opens without asking the coordinator");

            for (var i = 1; i <= 50; i++) docs.FindById(i % 20 + 1)["_id"].AsInt32.Should().Be(i % 20 + 1);

            reads.PageHits.Should().Be(hits + 50);
            reads.GrantOpens.Should().Be(grants, "no snapshot was granted over IPC");
            reads.IpcReads.Should().Be(ipc);
            reads.SnapshotOpens.Should().Be(opens, "nothing was committed, so no snapshot opened");
        }

        [Fact]
        public void Commits_advance_the_cached_snapshot_incrementally_and_match_a_fresh_open()
        {
            using var file = new TempFile();
            using var host = new CoordinatedEngine(file.Filename);
            using var client = new CoordinatedEngine(file.Filename);
            using var hostDb = new LiteDatabase(host, disposeOnClose: false);
            using var clientDb = new LiteDatabase(client, disposeOnClose: false);
            hostDb.Pragma(Pragmas.CHECKPOINT, 0);
            var hostDocs = hostDb.GetCollection("docs");
            hostDocs.EnsureIndex("value", "$.value");
            hostDocs.Insert(Enumerable.Range(1, 50).Select(i => new BsonDocument { ["_id"] = i, ["value"] = 0 }));
            var reads = client.ClientForTests;
            reads.VerifyRefresh = true;
            var docs = clientDb.GetCollection("docs");
            docs.Count().Should().Be(50);

            for (var round = 1; round <= 30; round++)
            {
                // Writes from both the coordinator and the client, including new collections and headers.
                if (round % 2 == 0) hostDocs.Update(new BsonDocument { ["_id"] = round, ["value"] = round });
                else docs.Update(new BsonDocument { ["_id"] = round, ["value"] = round });
                if (round % 10 == 0) clientDb.GetCollection("extra" + round).Insert(new BsonDocument { ["_id"] = round });
                docs.FindById(round)["value"].AsInt32.Should().Be(round);
                docs.Count(Query.GT("value", 0)).Should().Be(round);
            }

            reads.Refreshes.Should().BeGreaterOrEqualTo(25, "an idle snapshot advances by the appended frames");
            reads.VerifiedRefreshes.Should().BeGreaterOrEqualTo(25, "each advanced snapshot equals a fresh open");
            clientDb.GetCollectionNames().Should().Contain(new[] { "extra10", "extra20", "extra30" });
        }

        [Theory]
        [InlineData("page-read")]
        [InlineData("lease-registered")]
        [InlineData("engine-opened")]
        public void A_checkpoint_overlapping_the_handshake_rejects_the_snapshot(string stage)
        {
            using var file = new TempFile();
            using var host = new CoordinatedEngine(file.Filename);
            using var client = new CoordinatedEngine(file.Filename);
            using var hostDb = new LiteDatabase(host, disposeOnClose: false);
            using var clientDb = new LiteDatabase(client, disposeOnClose: false);
            hostDb.Pragma(Pragmas.CHECKPOINT, 0);
            hostDb.GetCollection("docs").Insert(Enumerable.Range(1, 30).Select(i => new BsonDocument { ["_id"] = i, ["v"] = 1 }));
            var reads = client.ClientForTests;
            var fired = 0;
            reads.HandshakeStage = name =>
            {
                // Only the first attempt races; the retry then opens undisturbed.
                if (name != stage || Interlocked.Exchange(ref fired, 1) != 0) return;
                RunThread(() => hostDb.Checkpoint());
            };

            clientDb.GetCollection("docs").FindAll().Select(x => x["v"].AsInt32).Should().HaveCount(30).And.OnlyContain(v => v == 1);
            fired.Should().Be(1);
            reads.HandshakeRejects.Should().Be(1, "the structural counter changed during the attempt");
            reads.HandshakeOpens.Should().Be(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Skipping_the_recheck_accepts_a_snapshot_whose_frames_a_concurrent_checkpoint_reclaims(bool skipRecheck)
        {
            using var file = new TempFile();
            using var host = new CoordinatedEngine(file.Filename);
            using var client = new CoordinatedEngine(file.Filename);
            using var hostDb = new LiteDatabase(host, disposeOnClose: false);
            using var clientDb = new LiteDatabase(client, disposeOnClose: false);
            hostDb.Pragma(Pragmas.CHECKPOINT, 0);
            var hostDocs = hostDb.GetCollection("docs");
            hostDocs.Insert(Enumerable.Range(1, 40).Select(i => Doc(i, 0)));
            // A local reader pins an old version, so the checkpoint below is partial:
            // it keeps that version and the newest, and reclaims what only others needed.
            using var pinned = new Pin(host);
            hostDocs.Update(Enumerable.Range(1, 40).Select(i => Doc(i, 1))).Should().Be(40);

            var engine = host.HostForTests.Engine;
            using var scanned = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            engine.CheckpointStage = name =>
            {
                if (name != "before-commit-lock") return;
                scanned.Set();
                resume.Wait(Wait);
            };
            Task checkpoint = null;
            var reads = client.ClientForTests;
            reads.UnsafeSkipRecheck = skipRecheck;
            reads.HandshakeStage = name =>
            {
                // The checkpoint scans the leases before this client registers one.
                if (name != "page-read" || checkpoint != null) return;
                checkpoint = Task.Run(() => host.HostForTests.Run(e => e.Checkpoint()));
                scanned.Wait(Wait).Should().BeTrue();
            };

            var docs = clientDb.GetCollection("docs");
            if (!skipRecheck)
            {
                // Rejected: the read is answered over IPC while the checkpoint is paused.
                docs.FindAll().Select(x => x["v"].AsInt32).Should().OnlyContain(v => v == 1).And.HaveCount(40);
                reads.HandshakeRejects.Should().BeGreaterThan(0);
                reads.HandshakeOpens.Should().Be(0);
                reads.IpcReads.Should().BeGreaterThan(0, "the grant waits for the running checkpoint call, so the read fell back to IPC");
                resume.Set();
                checkpoint.Wait(Wait).Should().BeTrue();
                return;
            }

            // Accepted without the recheck: open a reader on the unsafe snapshot at v=1.
            using var reader = client.Query("docs", new Query());
            reads.HandshakeOpens.Should().Be(1, "the negative control accepted the snapshot");
            // A newer commit supersedes v=1; the paused checkpoint, which never saw the
            // client's lease, then reclaims the frames only that snapshot still needs.
            hostDocs.Update(Enumerable.Range(1, 40).Select(i => Doc(i, 2))).Should().Be(40);
            resume.Set();
            checkpoint.Wait(Wait).Should().BeTrue();
            Action read = () =>
            {
                var values = new List<int>();
                while (reader.Read()) values.Add(reader.Current["v"].AsInt32);
                values.Should().HaveCount(40).And.OnlyContain(v => v == 1, "a snapshot must keep its version");
            };
            read.Should().Throw<Exception>("the snapshot's frames were reclaimed under it");
        }

        [Fact]
        public void A_reclaimed_slot_reused_after_a_snapshot_opened_prevents_its_incremental_refresh()
        {
            foreach (var ignoreReuse in new[] { false, true })
            {
                using var file = new TempFile();
                using var host = new CoordinatedEngine(file.Filename);
                using var client = new CoordinatedEngine(file.Filename);
                using var hostDb = new LiteDatabase(host, disposeOnClose: false);
                using var clientDb = new LiteDatabase(client, disposeOnClose: false);
                hostDb.Pragma(Pragmas.CHECKPOINT, 0);
                var hostDocs = hostDb.GetCollection("docs");
                hostDocs.Insert(Enumerable.Range(1, 40).Select(i => Doc(i, 0)));
                // Partial checkpoint under a local reader: superseded frames become free slots.
                using (new Pin(host))
                {
                    for (var v = 1; v <= 3; v++) hostDocs.Update(Enumerable.Range(1, 40).Select(i => Doc(i, v)));
                    RunThread(() => hostDb.Checkpoint());
                }
                var reads = client.ClientForTests;
                reads.UnsafeIgnoreReuse = ignoreReuse;
                reads.VerifyRefresh = true;
                var docs = clientDb.GetCollection("docs");
                docs.FindById(1)["v"].AsInt32.Should().Be(3);
                var (refreshes, rejects, epoch) = (reads.Refreshes, reads.RefreshRejects, Epoch(file.Filename));

                // This commit writes its payload into reclaimed slots before the client's rescan point.
                hostDocs.Update(Enumerable.Range(1, 40).Select(i => Doc(i, 4)));
                Epoch(file.Filename).Should().BeGreaterThan(epoch, "the commit reused reclaimed slots");
                docs.FindAll().Select(x => x["v"].AsInt32).Should().HaveCount(40).And.OnlyContain(v => v == 4);
                reads.Refreshes.Should().Be(refreshes);
                // Without the epoch check the refresh is attempted; the appended confirmation's
                // frame count then exposes the reused frames it cannot see, and it is rejected.
                reads.RefreshRejects.Should().Be(rejects + (ignoreReuse ? 1 : 0));
            }
        }

        [Fact]
        public void A_new_coordinator_invalidates_cached_snapshots_while_a_dead_ones_page_stays_usable()
        {
            using var file = new TempFile();
            using var first = new CoordinatedEngine(file.Filename);
            using var second = new CoordinatedEngine(file.Filename);
            using var reader = new CoordinatedEngine(file.Filename);
            using var firstDb = new LiteDatabase(first, disposeOnClose: false);
            using var secondDb = new LiteDatabase(second, disposeOnClose: false);
            using var readerDb = new LiteDatabase(reader, disposeOnClose: false);
            firstDb.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["v"] = "before" });
            var docs = readerDb.GetCollection("docs");
            docs.FindById(1)["v"].AsString.Should().Be("before");
            var reads = reader.ClientForTests;

            first.CrashCoordinator();
            // The dead coordinator's page is stale but accurate: nobody can write yet.
            var hits = reads.PageHits;
            docs.FindById(1)["v"].AsString.Should().Be("before");
            reads.PageHits.Should().Be(hits + 1);

            // Another participant takes over (a retry-safe call re-elects) and commits;
            // the page now names a new coordinator.
            _ = secondDb.UserVersion;
            second.IsCoordinator.Should().BeTrue();
            secondDb.GetCollection("docs").Update(new BsonDocument { ["_id"] = 1, ["v"] = "after" }).Should().BeTrue();
            second.IsCoordinator.Should().BeTrue();
            docs.FindById(1)["v"].AsString.Should().Be("after", "a cached snapshot never outlives its coordinator's page");
        }

        [Fact]
        public void Refreshed_and_opened_snapshots_match_a_direct_mode_oracle_under_checkpoints_and_reuse()
        {
            using var file = new TempFile();
            var expected = new Dictionary<int, int>();
            var random = new Random(3004);
            using (var host = new CoordinatedEngine(file.Filename))
            using (var a = new CoordinatedEngine(file.Filename))
            using (var b = new CoordinatedEngine(file.Filename))
            {
                var databases = new[] { host, a, b }.Select(x => new LiteDatabase(x, disposeOnClose: false)).ToArray();
                databases[0].Pragma(Pragmas.CHECKPOINT, 0);
                databases[0].GetCollection("docs").EnsureIndex("value", "$.value");
                a.ClientForTests.VerifyRefresh = true;
                b.ClientForTests.VerifyRefresh = true;
                Pin pinned = null;
                for (var step = 0; step < 600; step++)
                {
                    var db = databases[random.Next(databases.Length)].GetCollection("docs");
                    var id = random.Next(80);
                    var value = random.Next(1000);
                    switch (random.Next(7))
                    {
                        case 0:
                        case 1:
                            db.Upsert(new BsonDocument { ["_id"] = id, ["value"] = value, ["pad"] = new string('p', random.Next(2000)) });
                            expected[id] = value;
                            break;
                        case 2:
                            db.Delete(id).Should().Be(expected.Remove(id));
                            break;
                        case 3:
                            var found = db.FindById(id);
                            if (expected.TryGetValue(id, out var stored)) found["value"].AsInt32.Should().Be(stored);
                            else (found == null).Should().BeTrue();
                            break;
                        case 4:
                            db.Count(Query.GT("value", 500)).Should().Be(expected.Values.Count(x => x > 500));
                            break;
                        case 5:
                            // Partial and full checkpoints, with and without a pinned old version.
                            if (pinned == null && random.Next(2) == 0) pinned = new Pin(host);
                            else if (pinned != null)
                            {
                                pinned.Dispose();
                                pinned = null;
                            }
                            RunThread(() => databases[0].Checkpoint());
                            break;
                        default:
                            db.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(expected.Keys.OrderBy(x => x));
                            break;
                    }
                }
                pinned?.Dispose();
                foreach (var database in databases) database.Dispose();
                (a.ClientForTests.Refreshes + b.ClientForTests.Refreshes).Should().BeGreaterThan(0);
                (a.ClientForTests.VerifiedRefreshes + b.ClientForTests.VerifiedRefreshes).Should().BeGreaterThan(0);
                (a.ClientForTests.HandshakeOpens + b.ClientForTests.HandshakeOpens).Should().BeGreaterThan(0);
            }

            using var oracle = new LiteDatabase(file.Filename);
            oracle.GetCollection("docs").FindAll().ToDictionary(x => x["_id"].AsInt32, x => x["value"].AsInt32)
                .Should().Equal(expected);
        }

        /// <summary>
        /// A coordinator-local reader holding an old version on its own thread (a reader
        /// on the test thread would absorb that thread's later writes into its transaction).
        /// </summary>
        private sealed class Pin : IDisposable
        {
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim();
            private readonly Thread _thread;

            internal Pin(CoordinatedEngine host)
            {
                using var ready = new ManualResetEventSlim();
                Exception failure = null;
                _thread = new Thread(() =>
                {
                    try
                    {
                        using var reader = host.HostForTests.Run(e => e.Query("docs", new Query()));
                        reader.Read();
                        ready.Set();
                        _release.Wait();
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        ready.Set();
                    }
                });
                _thread.Start();
                ready.Wait();
                if (failure != null) throw new InvalidOperationException("Pinning reader failed.", failure);
            }

            public void Dispose()
            {
                _release.Set();
                _thread.Join();
                _release.Dispose();
            }
        }

        private static long Epoch(string filename)
        {
            using var page = LiteDB.Client.Coordinated.CoordinatorStatusPage.TryOpen(filename);
            page.TryRead(out var status).Should().BeTrue();
            return status.ReuseEpoch;
        }

        private static BsonDocument Doc(int id, int v) => new BsonDocument { ["_id"] = id, ["v"] = v, ["pad"] = new string('x', 1500) };

        // Checkpoints wait for this thread's own transaction lock otherwise.
        private static void RunThread(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.Start();
            thread.Join();
            if (failure != null) throw new InvalidOperationException("Background action failed.", failure);
        }
    }
}
#endif
