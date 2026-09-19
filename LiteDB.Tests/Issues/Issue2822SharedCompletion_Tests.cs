using System;
using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2822SharedCompletion_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Ordinary_shared_reader_does_not_look_like_an_explicit_transaction(bool separateInstance)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared };
            using var db = new LiteDatabase(connection);
            using var other = new LiteDatabase(connection);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            using var cursor = db.GetCollection("rows").FindAll().GetEnumerator();
            cursor.MoveNext().Should().BeTrue();
            Exception failure = null;
            var thread = new Thread(() =>
            {
                failure = Record.Exception(() =>
                {
                    var target = separateInstance ? other : db;
                    target.Commit().Should().BeFalse();
                    target.Rollback().Should().BeFalse();
                });
            });
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            failure.Should().BeNull();
            cursor.Current["_id"].AsInt32.Should().Be(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Joining_an_auto_transaction_does_not_require_an_extra_completion(bool rollback)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared };
            using var db = new LiteDatabase(connection);
            IEnumerable<BsonDocument> Documents()
            {
                db.BeginTrans().Should().BeFalse();
                db.BeginTrans().Should().BeFalse();
                yield return new BsonDocument { ["_id"] = 1 };
            }
            db.GetCollection("rows").Insert(Documents()).Should().Be(1);
            Exception failure = null;
            var other = new Thread(() => failure = Record.Exception(() =>
            {
                using var reopened = new LiteDatabase(connection);
                reopened.GetCollection("rows").Count().Should().Be(1);
                reopened.BeginTrans().Should().BeTrue();
                reopened.Rollback().Should().BeTrue();
            })) { IsBackground = true };
            other.Start();
            other.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            failure.Should().BeNull();
            (rollback ? db.Rollback() : db.Commit()).Should().BeFalse();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Ordinary_open_rejects_an_abandoned_local_transaction(bool begin)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared };
            using var db = new LiteDatabase(connection);
            Exception failure = null;
            var owner = new Thread(() => failure = Record.Exception(() => db.BeginTrans().Should().BeTrue()));
            owner.Start();
            owner.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            failure.Should().BeNull();
            Action open = () => { if (begin) db.BeginTrans(); else db.GetCollection("rows").Count(); };
            open.Should().Throw<LiteException>().WithMessage("*owner thread exited*");
            db.BeginTrans().Should().BeTrue();
            db.Rollback().Should().BeTrue();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Abandoned_explicit_owner_is_rejected_and_can_be_disposed_normally(bool rollback, bool consumeSignal)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared };
            var db = new LiteDatabase(connection);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            Exception failure = null;
            var owner = new Thread(() => failure = Record.Exception(() =>
            {
                db.BeginTrans().Should().BeTrue();
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
            }));
            owner.Start();
            owner.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            failure.Should().BeNull();
            if (consumeSignal)
            {
                using var second = new LiteDatabase(connection);
                // A fresh instance owns no transaction. Its completion acquires and
                // releases the abandoned named mutex without opening a file engine.
                second.Commit().Should().BeFalse();
            }
            Action complete = () => { if (rollback) db.Rollback(); else db.Commit(); };
            complete.Should().Throw<LiteException>().WithMessage("*owner thread exited*uncommitted work was discarded*");
            db.Dispose();
            using var reopened = new LiteDatabase(connection);
            reopened.GetCollection("rows").Count().Should().Be(1);
            reopened.BeginTrans().Should().BeTrue();
            reopened.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 });
            reopened.Commit().Should().BeTrue();
        }
    }
}
