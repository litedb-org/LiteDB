using System;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2822RollbackGuard_Tests
    {
        [Fact]
        public void Rollback_in_a_catch_block_keeps_the_original_error_while_another_thread_owns_a_transaction()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1 });

            using var other = new ForeignTransaction(db);
            Action unitOfWork = () =>
            {
                try
                {
                    db.BeginTrans();
                    rows.Insert(new BsonDocument { ["_id"] = 1 });
                    db.Commit();
                }
                catch
                {
                    db.Rollback();
                    throw;
                }
            };

            unitOfWork.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INDEX_DUPLICATE_KEY);
            other.Commit();
            db.GetCollection("other").Count().Should().Be(1);
            rows.Count().Should().Be(1);
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Defensive_rollback_on_an_idle_thread_returns_false_and_leaves_the_owner_untouched(ConnectionType connection)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = connection });

            using var other = new ForeignTransaction(db);

            db.Rollback().Should().BeFalse();
            other.Commit();
            db.GetCollection("other").Count().Should().Be(1);
        }

        [Fact]
        public void Commit_after_the_engine_aborted_this_threads_transaction_returns_false_once()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1 });

            using var other = new ForeignTransaction(db);
            db.BeginTrans().Should().BeTrue();
            Action duplicate = () => rows.Insert(new BsonDocument { ["_id"] = 1 });
            duplicate.Should().Throw<LiteException>();

            db.Commit().Should().BeFalse("the engine already rolled back this thread's transaction");
            Action foreignCommit = () => db.Commit();
            foreignCommit.Should().Throw<LiteException>("the abort was acknowledged").WithMessage("*same thread*");
            other.Commit();
            db.GetCollection("other").Count().Should().Be(1);
        }

        [Fact]
        public void A_new_transaction_forgets_an_earlier_engine_side_abort()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1 });

            db.BeginTrans().Should().BeTrue();
            Action duplicate = () => rows.Insert(new BsonDocument { ["_id"] = 1 });
            duplicate.Should().Throw<LiteException>();
            db.BeginTrans().Should().BeTrue();
            db.Commit().Should().BeTrue();

            using var other = new ForeignTransaction(db);
            Action foreignCommit = () => db.Commit();
            foreignCommit.Should().Throw<LiteException>().WithMessage("*same thread*");
            other.Commit();
        }

        /// <summary>
        /// Holds an explicit transaction with one pending insert on a dedicated thread.
        /// </summary>
        private sealed class ForeignTransaction : IDisposable
        {
            private readonly ManualResetEventSlim _finish = new ManualResetEventSlim();
            private readonly Thread _thread;
            private Exception _failure;
            private bool _committed;

            public ForeignTransaction(ILiteDatabase db)
            {
                using var pending = new ManualResetEventSlim();
                _thread = new Thread(() =>
                {
                    try
                    {
                        db.BeginTrans().Should().BeTrue();
                        db.GetCollection("other").Insert(new BsonDocument { ["_id"] = 1 });
                        pending.Set();
                        if (!_finish.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException();
                        _committed = db.Commit();
                    }
                    catch (Exception ex) { _failure = ex; }
                    finally { pending.Set(); }
                }) { IsBackground = true };
                _thread.Start();
                pending.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                _failure.Should().BeNull();
            }

            public void Commit()
            {
                this.Join();
                _failure.Should().BeNull();
                _committed.Should().BeTrue("the owner's transaction must survive other threads' completions");
            }

            public void Dispose()
            {
                this.Join();
                _finish.Dispose();
            }

            private void Join()
            {
                _finish.Set();
                _thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
            }
        }
    }
}
