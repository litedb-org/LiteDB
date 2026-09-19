using System;
using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2822CompletionGuard_Tests
    {
        [Theory]
        [InlineData(false, ConnectionType.Direct)]
        [InlineData(true, ConnectionType.Direct)]
        [InlineData(false, ConnectionType.Shared)]
        [InlineData(true, ConnectionType.Shared)]
        public void Foreign_completion_neither_commits_nor_rolls_back_the_owner(bool commit, ConnectionType connection)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = connection });
            db.BeginTrans().Should().BeTrue();
            db.BeginTrans().Should().BeFalse("nested begin joins the same transaction");
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            Exception failure = null;
            var rolledBack = true;
            var foreign = new Thread(() => failure = Record.Exception(() =>
            {
                if (commit) db.Commit(); else rolledBack = db.Rollback();
            }));
            foreign.Start();
            foreign.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            if (commit) failure.Should().BeOfType<LiteException>().Which.Message.Should().Contain("same thread").And.Contain("await");
            else
            {
                // Rollback lives in catch/finally blocks: it reports "nothing to roll back" instead of throwing.
                failure.Should().BeNull();
                rolledBack.Should().BeFalse();
            }
            db.GetCollection("rows").Count().Should().Be(1);
            db.Commit().Should().BeTrue("foreign misuse must leave the owner transaction active");
            db.GetCollection("rows").Count().Should().Be(1);
            db.Commit().Should().BeFalse();
            db.Rollback().Should().BeFalse();
        }
        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void A_false_join_to_an_active_auto_transaction_is_not_an_explicit_owner(ConnectionType connection)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = connection });
            IEnumerable<BsonDocument> Documents()
            {
                db.BeginTrans().Should().BeFalse();
                Exception failure = null;
                var foreign = new Thread(() => failure = Record.Exception(() =>
                {
                    db.Commit().Should().BeFalse();
                    db.Rollback().Should().BeFalse();
                }));
                foreign.Start();
                foreign.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                failure.Should().BeNull();
                yield return new BsonDocument { ["_id"] = 1 };
            }
            db.GetCollection("rows").Insert(Documents()).Should().Be(1);
            db.GetCollection("rows").Count().Should().Be(1);
        }
    }
}
