#if NET8_0_OR_GREATER
using System;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedOneFileSlice7OwnerReuseReview_Tests
    {
        [OneFileFact]
        public void Begin_queued_behind_dead_owner_cleanup_publishes_a_new_local_owner()
        {
            using var file = new MappedTestFile();
            using (var seed = new LiteDatabase(file.Filename))
                seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            var settings = new EngineSettings { Filename = file, SharedOneFile = true };
            using var first = new SharedEngine(settings);
            using var second = new SharedEngine(settings);
            using var owner = new LiteDatabase(first, disposeOnClose: false);
            using var peer = new LiteDatabase(second, disposeOnClose: false);
            using var cleanupEntered = new ManualResetEventSlim();
            using var releaseCleanup = new ManualResetEventSlim();
            first.OwnershipStage = stage =>
            {
                if (stage != "before-tenure-close") return;
                cleanupEntered.Set();
                releaseCleanup.Wait(TimeSpan.FromSeconds(10));
            };
            Exception oldFailure = null, successorFailure = null, foreignFailure = null;
            var oldThread = new Thread(() => oldFailure = Record.Exception(() =>
            {
                owner.BeginTrans().Should().BeTrue();
                owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 });
            })) { IsBackground = true };
            var successor = new Thread(() => successorFailure = Record.Exception(() =>
            {
                try
                {
                    var started = false;
                    var abandonment = Record.Exception(() => started = owner.BeginTrans());
                    if (abandonment != null)
                    {
                        abandonment.Should().BeOfType<LiteException>().Which.Message.Should().Contain("owner thread exited");
                        started = owner.BeginTrans();
                    }
                    started.Should().BeTrue();
                    owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 3 });
                    foreignFailure = Record.Exception(() => peer.Rollback());
                }
                finally { owner.Rollback(); }
            })) { IsBackground = true };
            var successorStarted = false;
            oldThread.Start();
            try
            {
                oldThread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                oldFailure.Should().BeNull();
                cleanupEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                successor.Start();
                successorStarted = true;
                SpinWait.SpinUntil(() => Field<int>(first, "_mutexWaiters") > 0,
                    TimeSpan.FromSeconds(5)).Should().BeTrue("Begin must pass its local-lease decision before old ownership cleanup resumes");
            }
            finally
            {
                releaseCleanup.Set();
                if (successorStarted) successor.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                first.OwnershipStage = null;
            }
            successorFailure.Should().BeNull();
            first.Dispose();
            second.Dispose();
            using (var cold = new LiteDatabase(file.Filename))
                cold.GetCollection("docs").FindAll().Should().BeEquivalentTo(new[] { new BsonDocument { ["_id"] = 1 } });
            foreignFailure.Should().BeOfType<InvalidOperationException>(
                "the new explicit transaction must publish its owner even when Begin started before old-owner cleanup")
                .Which.Message.Should().Contain("connection");
        }

        private static T Field<T>(object value, string name) =>
            (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
    }
}
#endif
