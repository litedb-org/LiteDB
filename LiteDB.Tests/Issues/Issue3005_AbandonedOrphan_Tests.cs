using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    /// <summary>
    /// #3005: an explicit transaction's owner thread exits, which abandons the named
    /// mutex. Another connection then acquires it and commits. When the first
    /// connection next acquires the mutex, it rejects the abandoned transaction and
    /// discards its engine. That engine's WAL index and cache predate the other
    /// connection's commits, so it must be dropped without running a checkpoint.
    /// On Windows the orphan's writable handle (FileShare.Read) keeps the peer out
    /// entirely; on Unix, FileShare maps to shared advisory locks and the peer can
    /// commit meanwhile, so the peer scenario runs there only.
    /// </summary>
    public class Issue3005_AbandonedOrphan_Tests
    {
        private const int RowCount = 64;

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void Orphaned_engine_is_discarded_without_checkpointing_a_stale_view(string password, bool readerHeldThroughout)
        {
            using var file = new TempFile();
            var stages = new ConcurrentQueue<string>();
            var armed = false;
            var orphanSettings = Settings(file.Filename, password);
            // Safepoints write the abandoned transaction's pages, so its WAL is not empty.
            orphanSettings.TransactionPageLimit = 1;
            orphanSettings.CheckpointStage = stage => { if (Volatile.Read(ref armed)) stages.Enqueue(stage); };

            using var orphanEngine = new SharedEngine(orphanSettings);
            using var orphan = new LiteDatabase(orphanEngine, disposeOnClose: false);
            orphan.GetCollection("rows").Insert(Rows(0));

            Exception failure = null;
            var owner = new Thread(() => failure = Record.Exception(() =>
            {
                orphan.BeginTrans().Should().BeTrue();
                orphan.GetCollection("rows").Update(Rows(99));
            }));
            owner.Start();
            owner.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
            failure.Should().BeNull();

            var peerCommits = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            using (var peerEngine = peerCommits ? new SharedEngine(Settings(file.Filename, password)) : null)
            using (var peer = peerCommits ? new LiteDatabase(peerEngine, disposeOnClose: false) : null)
            {
                // The peer consumes the abandonment and commits, before and after a
                // checkpoint; with a live reader that checkpoint is partial.
                IBsonDataReader reader = null;
                if (peerCommits)
                {
                    if (readerHeldThroughout)
                    {
                        peer.GetCollection("rows").Update(Rows(1));
                        reader = peerEngine.Query("rows", new Query());
                    }
                    peer.GetCollection("rows").Update(Rows(5));
                    peer.GetCollection("extra").Insert(Enumerable.Range(1, 200).Select(id =>
                        new BsonDocument { ["_id"] = id, ["payload"] = new string('y', 500) }));
                    peer.GetCollection("extra").EnsureIndex("payload");
                    peer.Checkpoint();
                    peer.GetCollection("rows").Update(Rows(6));
                }

                Volatile.Write(ref armed, true);
                Action rejected = () => orphan.GetCollection("rows").Count();
                rejected.Should().Throw<LiteException>().WithMessage("*owner thread exited*");
                Volatile.Write(ref armed, false);
                stages.Should().BeEmpty("the orphaned engine's view may predate another connection's commits");

                if (reader != null)
                {
                    var values = 0;
                    while (reader.Read())
                    {
                        reader.Current["value"].AsInt32.Should().Be(1, "the peer's reader keeps its snapshot");
                        values++;
                    }
                    values.Should().Be(RowCount);
                    reader.Dispose();
                }
            }

            var latest = peerCommits ? 6 : 0;
            orphan.GetCollection("rows").FindAll().Should().OnlyContain(doc => doc["value"].AsInt32 == latest);
            orphanEngine.Dispose();
            AssertLatest(file.Filename, password, latest, peerCommits);
        }

        private static void AssertLatest(string filename, string password, int latest, bool peerCommits)
        {
            using var engine = new LiteEngine(Settings(filename, password));
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var rows = db.GetCollection("rows");
            rows.FindAll().Should().HaveCount(RowCount).And.OnlyContain(doc => doc["value"].AsInt32 == latest);
            Enumerable.Range(0, RowCount).Should().OnlyContain(id => rows.FindById(id) != null);
            if (peerCommits)
            {
                var extra = db.GetCollection("extra");
                extra.Count().Should().Be(200);
                extra.Count(Query.EQ("payload", new string('y', 500))).Should().Be(200);
                Enumerable.Range(1, 200).Should().OnlyContain(id => extra.FindById(id) != null);
            }
            rows.Update(Rows(7));
            db.Checkpoint();
            rows.FindAll().Should().OnlyContain(doc => doc["value"].AsInt32 == 7);
        }

        private static EngineSettings Settings(string filename, string password) =>
            new EngineSettings { Filename = filename, Password = password };

        private static BsonDocument[] Rows(int value) => Enumerable.Range(0, RowCount).Select(id =>
            new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 3000) }).ToArray();
    }
}
