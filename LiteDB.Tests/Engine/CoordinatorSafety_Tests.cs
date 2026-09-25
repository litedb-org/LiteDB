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
    }
}
#endif
