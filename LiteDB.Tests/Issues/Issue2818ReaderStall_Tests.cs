using System;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using GatedDurableFile = LiteDB.Tests.Issues.Issue2818_Tests.GatedDurableFile;

namespace LiteDB.Tests.Issues
{
    public class Issue2818ReaderStall_Tests
    {
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task Read_of_another_collection_completes_while_a_commit_waits_for_its_durable_flush()
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            using var data = new GatedDurableFile(dataFile.Filename);
            using var log = new GatedDurableFile(logFile.Filename);
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var written = db.GetCollection("written");
            var read = db.GetCollection("read");
            written.Insert(new BsonDocument { ["_id"] = 1, ["v"] = 0 });
            read.Insert(new BsonDocument { ["_id"] = 1, ["v"] = 7 });
            db.Checkpoint();

            log.ArmDurableFlushGate();
            var flushEntered = log.DurableFlushEntered;
            var commit = Task.Run(() => written.Update(new BsonDocument { ["_id"] = 1, ["v"] = 1 }));
            Task<BsonDocument> otherCollection = null;
            Task<BsonDocument> sameCollection = null;
            Task firstOther;
            Task firstSame;
            bool commitCompletedWhileFlushWasHeld;

            try
            {
                (await Task.WhenAny(flushEntered, Task.Delay(_timeout))).Should().BeSameAs(flushEntered,
                    "the commit must reach its durable log flush");
                otherCollection = Task.Run(() => read.FindById(1));
                sameCollection = Task.Run(() => written.FindById(1));
                firstOther = await Task.WhenAny(otherCollection, Task.Delay(_timeout));
                firstSame = await Task.WhenAny(sameCollection, Task.Delay(_timeout));
                commitCompletedWhileFlushWasHeld = commit.IsCompleted;
            }
            finally
            {
                log.ReleaseDurableFlush();
            }

            (await Task.WhenAny(commit, Task.Delay(_timeout))).Should().BeSameAs(commit);
            (await commit).Should().BeTrue();
            commitCompletedWhileFlushWasHeld.Should().BeFalse("the gate still held the commit's fsync");
            firstOther.Should().BeSameAs(otherCollection, "a reader must not queue behind another commit's fsync");
            firstSame.Should().BeSameAs(sameCollection, "readers are never blocked by a writer's collection lock");
            (await otherCollection)["v"].AsInt32.Should().Be(7);
            (await sameCollection)["v"].AsInt32.Should().Be(0,
                "a transaction stays invisible until its confirmation is durable and indexed");
            written.FindById(1)["v"].AsInt32.Should().Be(1);
        }
    }
}
