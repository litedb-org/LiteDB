using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CheckpointAdmissionIntent_Tests
    {
        [Theory]
        [InlineData("pragma")]
        [InlineData("index")]
        [InlineData("collection")]
        [InlineData("for-update")]
        [InlineData("explicit")]
        public void FreshWriteIntentCannotBypassPressure(string operation)
        {
            using var scenario = new CheckpointAdmissionScenario();
            var db = scenario.Database;
            db.BeginTrans();
            db.GetCollection("rows").Insert(CheckpointAdmissionScenario.Row(1));
            scenario.Monitor.GetThreadTransaction().Safepoint();
            scenario.Disk.WriteAdmission.UnderPressure.Should().BeTrue();
            var tail = scenario.Disk.GetFileLength(FileOrigin.Log);
            var error = Task.Run(() => Record.Exception(() =>
            {
                switch (operation)
                {
                    case "pragma": db.UserVersion = 999; break;
                    case "index": db.GetCollection("other").EnsureIndex("key"); break;
                    case "collection": db.DropCollection("rows"); break;
                    case "for-update":
                        using (var reader = scenario.Engine.Query("rows", new Query { ForUpdate = true })) reader.Read();
                        break;
                    case "explicit": db.BeginTrans(); break;
                }
            })).GetAwaiter().GetResult();
            error.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.LOCK_TIMEOUT);
            scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(tail);
            scenario.Disk.WriteAdmission.Writers.Should().Be(1);
            scenario.Monitor.Transactions.Should().HaveCount(1);
            db.UserVersion.Should().Be(0);
            db.GetCollectionNames().Should().Contain("rows").And.NotContain("other");
            db.Rollback();
            scenario.Disk.WriteAdmission.Writers.Should().Be(0);
            db.Checkpoint();
            scenario.VerifyRecovery(false);
        }

        // Independent event-history oracle: derive hysteresis from the last
        // decisive observation, rather than replaying the gate's mutable state.
        private static bool PressureFromHistory(int checkpoint, System.Collections.Generic.List<long> history) =>
            checkpoint > 0 && history.Where(pages => pages <= checkpoint || pages >= 4L * checkpoint)
                .LastOrDefault() >= 4L * checkpoint;

        [Fact]
        public void SeededAdmissionModelPreservesCapHysteresisAndInheritedOwners()
        {
            var random = new Random(73921);
            for (var round = 0; round < 8; round++)
            {
                var gate = new CheckpointWriteAdmission(1, 0);
                var permits = new System.Collections.Generic.List<CheckpointWriteAdmission.Permit>();
                var checkpoint = 1;
                long tail = 0;
                var history = new System.Collections.Generic.List<long> { 0 };
                for (var step = 0; step < 1000; step++)
                {
                    switch (random.Next(4))
                    {
                        case 0:
                            tail = random.Next(24);
                            gate.PublishTail(tail * Constants.PAGE_SIZE);
                            history.Add(tail);
                            break;
                        case 1:
                            var next = random.Next(5);
                            if (next != checkpoint) { history.Clear(); history.Add(tail); }
                            checkpoint = next;
                            gate.Configure(checkpoint, tail * Constants.PAGE_SIZE);
                            break;
                        case 2:
                            var allowed = checkpoint == 0 || (!PressureFromHistory(checkpoint, history) && permits.Count < 64);
                            CheckpointWriteAdmission.Permit permit = null;
                            var failure = Record.Exception(() => permit = gate.Acquire(TimeSpan.Zero, false, () => { }));
                            if (allowed) { failure.Should().BeNull(); permits.Add(permit); }
                            else failure.Should().BeOfType<LiteException>();
                            break;
                        case 3:
                            if (permits.Count == 0) break;
                            var index = random.Next(permits.Count);
                            permits[index].Dispose();
                            permits[index].Dispose();
                            permits.RemoveAt(index);
                            break;
                    }
                    gate.Writers.Should().Be(permits.Count);
                    gate.UnderPressure.Should().Be(PressureFromHistory(checkpoint, history));
                }
                foreach (var permit in permits) permit.Dispose();
                gate.Writers.Should().Be(0);
            }
        }
    }
}
