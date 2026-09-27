#if !NETFRAMEWORK && (DEBUG || TESTING)
#pragma warning disable LITEDB_EXPERIMENTAL_COORDINATOR
using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Coordinated;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Coordinator failure paths where a client could otherwise keep a snapshot that no
    /// longer protects what it reads: an expired grant, a takeover after a crash that left
    /// no abandoned mutex, and an automatic rebuild under a surviving snapshot.
    /// </summary>
    [Collection(CoordinatorPlatform_Tests.Collection)]
    public class CoordinatorSafety_Tests
    {
        private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(30);
        private const int Rows = 300;

        private static BsonDocument Doc(int id, string value = "v") =>
            new BsonDocument { ["_id"] = id, ["v"] = value, ["p"] = new string('p', 400) };

        /// <summary>
        /// The host keeps its gate closed from a grant until the client answers, or until the
        /// grant times out. A client that leases and opens after that timeout (here: after a
        /// checkpoint already found no lease and reclaims the WAL) must not keep the snapshot.
        /// </summary>
        [Fact]
        public void A_snapshot_opened_after_its_grant_expired_is_never_kept()
        {
            using var file = new TempFile();
            CoordinatedEngine host = null, reader = null;
            // Without a status page every client snapshot comes from an IPC grant.
            CoordinatorStatusPage.FailCreate = true;
            try
            {
                host = new CoordinatedEngine(file.Filename);
                reader = new CoordinatedEngine(file.Filename);
            }
            finally
            {
                CoordinatorStatusPage.FailCreate = false;
            }
            var previousTimeout = CoordinatorHost.GrantOpenTimeout;
            using (host)
            using (reader)
            {
                var hostDb = new LiteDatabase(host, disposeOnClose: false);
                hostDb.Pragma(Pragmas.CHECKPOINT, 0);
                hostDb.GetCollection("docs").Insert(Enumerable.Range(1, Rows).Select(id => Doc(id)));
                var client = reader.ClientForTests;
                var grantOpens = client.GrantOpens;

                using var granted = new ManualResetEventSlim();
                using var resume = new ManualResetEventSlim();
                using var scanned = new ManualResetEventSlim();
                using var finish = new ManualResetEventSlim();
                client.HandshakeStage = stage =>
                {
                    if (stage != "grant-received" || granted.IsSet) return;
                    granted.Set();
                    resume.Wait(Prompt);
                };
                host.HostForTests.Engine.CheckpointStage = stage =>
                {
                    if (stage != "before-commit-lock" || scanned.IsSet) return;
                    scanned.Set();
                    finish.Wait(Prompt);
                };
                CoordinatorHost.GrantOpenTimeout = TimeSpan.FromMilliseconds(200);
                try
                {
                    var seen = 0;
                    Exception readError = null;
                    var readerThread = new Thread(() =>
                    {
                        try
                        {
                            using var cursor = reader.Query("docs", new Query());
                            // Stream across the reclaiming checkpoint below.
                            if (cursor.Read()) seen++;
                            finish.Set();
                            Thread.Sleep(300);
                            while (cursor.Read())
                            {
                                cursor.Current["p"].AsString.Length.Should().Be(400);
                                seen++;
                            }
                        }
                        catch (Exception ex) { readError = ex; }
                    }) { IsBackground = true };
                    readerThread.Start();
                    granted.Wait(Prompt).Should().BeTrue();

                    // The grant expires and the host reopens its gate; a checkpoint then scans
                    // the registry, finds no lease, and pauses before it reclaims the WAL.
                    Thread.Sleep(600);
                    var checkpoint = new Thread(() => hostDb.Checkpoint()) { IsBackground = true };
                    checkpoint.Start();
                    scanned.Wait(Prompt).Should().BeTrue();

                    // The client leases and opens only now, after the decisive scan.
                    resume.Set();
                    // With an unprotected snapshot, the reader signals after its first row; otherwise
                    // the checkpoint is released so the IPC fallback can pass the gate.
                    finish.Wait(TimeSpan.FromSeconds(2));
                    finish.Set();
                    checkpoint.Join(Prompt).Should().BeTrue();
                    readerThread.Join(Prompt).Should().BeTrue();

                    readError.Should().BeNull();
                    seen.Should().Be(Rows);
                    client.GrantOpens.Should().Be(grantOpens, "a snapshot opened after its grant expired is discarded");
                }
                finally
                {
                    CoordinatorHost.GrantOpenTimeout = previousTimeout;
                    client.HandshakeStage = null;
                    resume.Set();
                    finish.Set();
                }
            }
        }

        /// <summary>
        /// A coordinator that crashed as the last holder of the named election mutex leaves no
        /// abandoned mutex: the successor acquires a fresh one. If that successor cannot write
        /// the dead coordinator's page, clients still trusting it must not read its snapshot
        /// after the successor committed.
        /// </summary>
        [Fact]
        public void A_takeover_without_an_abandoned_mutex_never_serves_the_dead_coordinators_snapshot()
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
            var hits = reader.ClientForTests.PageHits;
            docs.FindById(1)["v"].AsString.Should().Be("before");
            reader.ClientForTests.PageHits.Should().BeGreaterThan(hits, "the reader trusts the live coordinator's page");

            CoordinatorStatusPage.FailCreate = true;
            try
            {
                first.CrashCoordinator(abandonMutex: false);
                _ = secondDb.UserVersion;
                second.IsCoordinator.Should().BeTrue();
                secondDb.GetCollection("docs").Update(new BsonDocument { ["_id"] = 1, ["v"] = "after" }).Should().BeTrue();
            }
            finally
            {
                CoordinatorStatusPage.FailCreate = false;
            }

            for (var i = 0; i < 20; i++)
                docs.FindById(1)["v"].AsString.Should().Be("after", "a dead coordinator's page is never trusted after a successor committed");
        }

        /// <summary>
        /// Only a coordinator that died leaves its marker, so a first coordinator and a successor
        /// after a graceful stop start without the heartbeat wait, and the marker is gone again
        /// once the last coordinator stopped gracefully.
        /// </summary>
        [Fact]
        public void Only_a_dead_coordinator_makes_its_successor_wait()
        {
            using var file = new TempFile();
            var marker = CoordinatorMarker.PathFor(file.Filename);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var first = new CoordinatedEngine(file.Filename);
            first.IsCoordinator.Should().BeTrue();
            File.Exists(marker).Should().BeTrue("a coordinator holds its marker");
            using var second = new CoordinatedEngine(file.Filename);
            var secondDb = new LiteDatabase(second, disposeOnClose: false);
            secondDb.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            first.Dispose();

            _ = secondDb.UserVersion;
            second.IsCoordinator.Should().BeTrue();
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(CoordinatorStatusPage.HeartbeatTimeoutMilliseconds),
                "neither the first start nor a takeover after a graceful stop waits out a heartbeat");

            second.CrashCoordinator(abandonMutex: false);
            File.Exists(marker).Should().BeTrue("a dead coordinator leaves its marker");
            clock.Restart();
            // No coordinator is alive, so this engine is elected while it is constructed.
            using var third = new CoordinatedEngine(file.Filename);
            _ = new LiteDatabase(third, disposeOnClose: false).UserVersion;
            third.IsCoordinator.Should().BeTrue();
            clock.Elapsed.Should().BeGreaterOrEqualTo(TimeSpan.FromMilliseconds(CoordinatorStatusPage.HeartbeatTimeoutMilliseconds));
            third.Dispose();
            File.Exists(marker).Should().BeFalse("a graceful stop removes the marker");
        }

        /// <summary>
        /// A client's snapshot survives its coordinator. A successor opening a file marked invalid
        /// with AutoRebuild must not replace the files under that leased snapshot.
        /// </summary>
        [Fact]
        public void A_successor_does_not_auto_rebuild_under_a_surviving_snapshot()
        {
            using var file = new TempFile();
            using var first = new CoordinatedEngine(file.Filename);
            using var second = new CoordinatedEngine(new EngineSettings { Filename = file.Filename, AutoRebuild = true });
            using var reader = new CoordinatedEngine(file.Filename);
            new LiteDatabase(first, disposeOnClose: false).GetCollection("docs").Insert(Enumerable.Range(1, Rows).Select(id => Doc(id)));

            using var cursor = reader.Query("docs", new Query());
            cursor.Read().Should().BeTrue();
            reader.DirectReads.Should().BeGreaterThan(0, "the cursor reads a leased snapshot");

            first.CrashCoordinator();
            MarkInvalidState(file.Filename);
            Exception takeover = null;
            try { _ = new LiteDatabase(second, disposeOnClose: false).UserVersion; }
            catch (Exception ex) { takeover = ex; }

            Directory.GetFiles(Path.GetDirectoryName(file.Filename), Path.GetFileNameWithoutExtension(file.Filename) + "*")
                .Should().NotContain(path => path.Contains("-backup"), "a live snapshot lease blocks the automatic rebuild");
            var seen = 1;
            while (cursor.Read())
            {
                cursor.Current["p"].AsString.Length.Should().Be(400);
                seen++;
            }
            seen.Should().Be(Rows, "the surviving snapshot keeps reading the files it opened");
            takeover.Should().BeNull();
        }

        private static void MarkInvalidState(string filename)
        {
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[Constants.PAGE_SIZE];
            stream.Read(header, 0, header.Length);
            header[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
            PageChecksum.Write(new BufferSlice(header, 0, Constants.PAGE_SIZE));
            stream.Position = 0;
            stream.Write(header, 0, header.Length);
        }
    }
}
#endif
