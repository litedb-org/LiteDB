using System;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2822SharedCleanupRace_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Completion_waits_for_cleanup_that_already_claimed_the_exited_owner(bool rollback)
        {
            using var file = new TempFile();
            using var cleaning = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            using var completing = new ManualResetEventSlim();
            using var completed = new ManualResetEventSlim();
            using (var engine = new SharedEngine(new EngineSettings { Filename = file }))
            using (var database = new LiteDatabase(engine))
            {
                database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                engine.MutexOwner.BeforeOwnerExitedCleanup = () =>
                {
                    cleaning.Set();
                    proceed.Wait(TimeSpan.FromSeconds(10));
                };
                Exception ownerError = null;
                var owner = new Thread(() => ownerError = Record.Exception(() =>
                {
                    database.BeginTrans().Should().BeTrue();
                    database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                }));
                owner.Start();
                owner.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
                ownerError.Should().BeNull();
                Exception completionError = null;
                Thread completion = null;
                try
                {
                    cleaning.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    completion = new Thread(() =>
                    {
                        completing.Set();
                        completionError = Record.Exception(() => { if (rollback) database.Rollback(); else database.Commit(); });
                        completed.Set();
                    });
                    completion.Start();
                    completing.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    completed.Wait(TimeSpan.FromMilliseconds(100)).Should().BeFalse("the claimed cleanup still owns this connection");
                }
                finally
                {
                    proceed.Set();
                    completion?.Join(TimeSpan.FromSeconds(10));
                    engine.MutexOwner.BeforeOwnerExitedCleanup = null;
                }
                completed.IsSet.Should().BeTrue();
                completionError.Should().BeOfType<LiteException>().Which.Message.Should().Contain("owner thread exited")
                    .And.Contain("uncommitted work was discarded");
            }
            using var cold = new LiteDatabase(file);
            cold.GetCollection("rows").Count().Should().Be(1);
            Assert.NotNull(cold.GetCollection("rows").FindById(1));
            Assert.Null(cold.GetCollection("rows").FindById(2));
            cold.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 });
        }
    }
}
