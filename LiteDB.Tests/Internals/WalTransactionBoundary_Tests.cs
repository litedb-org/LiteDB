using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class WalTransactionBoundary_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void RepeatedSafepoints_ReusePositions_AndCommitAfterTheLastFlush(string password)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("docs");
            test.Database.Checkpoint();
            test.Database.BeginTrans();
            var transaction = test.Engine.GetMonitor().GetThreadTransaction();

            test.Update("docs", 1);
            transaction.Safepoint();
            var positions = transaction.Pages.DirtyPages.ToDictionary(pair => pair.Key, pair => pair.Value.Position);
            positions.Should().NotBeEmpty();
            var length = test.Log.Length;

            for (var value = 2; value <= 32; value++)
            {
                test.Update("docs", value);
                transaction.Safepoint();
                transaction.Pages.TransactionSize.Should().Be(0);
                test.Log.Length.Should().Be(length, "flushing the same pages must not append more slots");
                transaction.Pages.DirtyPages.ToDictionary(pair => pair.Key, pair => pair.Value.Position)
                    .Should().Equal(positions);
            }

            AssertValues(test.Recover("docs", checkpoint: false), 0);
            test.Database.Commit().Should().BeTrue();
            AssertValues(test.Database.GetCollection("docs").FindAll().ToArray(), 32);
            AssertValues(test.Recover("docs", checkpoint: false), 32);
            AssertValues(test.Recover("docs", checkpoint: true), 32);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void InterleavedTransactions_KeepTheirOwnSlots_AndRecoverInCommitOrder(string password, bool rollbackFirst)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("first");
            test.Seed("second");
            test.Database.Checkpoint();
            test.Database.BeginTrans();
            test.Update("first", 1);
            var first = test.Engine.GetMonitor().GetThreadTransaction();
            first.Safepoint();
            var firstPositions = first.Pages.DirtyPages.Values.Select(page => page.Position).ToArray();

            RunOnThread(() =>
            {
                test.Database.BeginTrans();
                test.Update("second", 2);
                var second = test.Engine.GetMonitor().GetThreadTransaction();
                second.Safepoint();
                second.Pages.DirtyPages.Values.Select(page => page.Position).Should().NotIntersectWith(firstPositions);
                test.Update("second", 3);
                test.Database.Commit().Should().BeTrue();
            });

            test.Update("first", 4);
            first.Safepoint();
            AssertValues(test.Recover("first", checkpoint: false), 0);
            AssertValues(test.Recover("second", checkpoint: false), 3);

            if (rollbackFirst) test.Database.Rollback().Should().BeTrue();
            else test.Database.Commit().Should().BeTrue();

            var expected = rollbackFirst ? 0 : 4;
            AssertValues(test.Recover("first", checkpoint: true), expected);
            AssertValues(test.Recover("second", checkpoint: true), 3);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void ReusedSlots_DoNotChangeAnOlderReadersCommittedWalVersion(string password)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("docs");
            test.Database.Checkpoint();
            test.Update("docs", 1);
            test.Log.Length.Should().BeGreaterThan(0, "the reader must observe a committed WAL version");
            test.Database.BeginTrans();
            var collection = test.Database.GetCollection("docs");
            AssertValues(collection.FindAll().ToArray(), 1);

            try
            {
                RunOnThread(() =>
                {
                    test.Database.BeginTrans();
                    for (var value = 2; value <= 8; value++)
                    {
                        test.Update("docs", value);
                        test.Engine.GetMonitor().GetThreadTransaction().Safepoint();
                    }
                    test.Update("docs", 9);
                    test.Database.Commit().Should().BeTrue();
                });

                AssertValues(collection.FindAll().ToArray(), 1);
                AssertValues(test.Recover("docs", checkpoint: false), 9);
            }
            finally
            {
                test.Database.Rollback();
            }
            AssertValues(collection.FindAll().ToArray(), 9);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CommitWithoutChanges_DoesNotAppendAConfirmation(bool writeSnapshot)
        {
            using var test = new WalTestDatabase(null);
            test.Seed("docs");
            test.Database.Checkpoint();
            var bytes = test.Log.ToArray();
            test.Database.BeginTrans();
            test.Engine.GetMonitor().GetThreadTransaction()
                .CreateSnapshot(writeSnapshot ? LockMode.Write : LockMode.Read, "docs", false);
            test.Database.Commit().Should().BeTrue();
            test.Log.ToArray().Should().Equal(bytes);
            AssertValues(test.Recover("docs", checkpoint: false), 0);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void FailedConfirmationAfterTheLastFlush_ReleasesItsFrameAndRemainsUncommitted(string password)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("docs");
            test.Database.Checkpoint();
            test.Database.BeginTrans();
            test.Update("docs", 1);
            var transaction = test.Engine.GetMonitor().GetThreadTransaction();
            transaction.Safepoint();
            var cache = transaction.Snapshots.Single().CollectionPage.Buffer.Cache;
            var writable = cache.WritablePages;
            var length = test.Log.Length;
            test.Engine.SimulateDiskWriteFail = page =>
            {
                if (page.ReadBool(BasePage.P_IS_CONFIRMED)) throw new IOException("injected confirmation failure");
            };
            try
            {
                Action commit = () => test.Database.Commit();
                commit.Should().Throw<IOException>().WithMessage("injected confirmation failure");
                test.Log.Length.Should().Be(length);
                cache.WritablePages.Should().Be(writable);
                cache.PinnedPages.Should().Be(0);
                cache.LostFrames.Should().Be(0);
                AssertValues(test.Recover("docs", checkpoint: false), 0);
            }
            finally
            {
                test.Engine.SimulateDiskWriteFail = null;
                test.Database.Rollback();
            }
            cache.WritablePages.Should().Be(0);
            test.Engine.GetMonitor().Transactions.Should().BeEmpty();
            AssertValues(test.Recover("docs", checkpoint: true), 0);
        }

        private static void AssertValues(BsonDocument[] documents, int value)
        {
            documents.Select(document => document["_id"].AsInt32).OrderBy(id => id)
                .Should().Equal(Enumerable.Range(0, WalTestDatabase.DocumentCount));
            documents.Select(document => document["value"].AsInt32).Should().OnlyContain(actual => actual == value);
        }

        private static void RunOnThread(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            }) { IsBackground = true };
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue("the worker must finish without a lock leak");
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
