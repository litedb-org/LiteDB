using System;
using System.Collections.Generic;
using Xunit;
using static LiteDB.Tests.Engine.TransactionHandleSharedCallback_Tests;
using static LiteDB.Tests.Engine.TransactionHandleOrdinaryCallbackBegin_Tests;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleOrdinaryCallbackControl_Tests
    {
        [Theory]
        [InlineData(null)] [InlineData("secret")]
        public void Nested_other_database_calls_restore_outer_ownership_after_exception(string password)
        {
            using var first = new TempFile();
            using var second = new TempFile();
            Seed(first, password);
            Seed(second, password);
            var settings = Settings(first, password);
            settings.ReadTransform = (_, value) => value;
            using (var shared = new SharedEngine(settings))
            using (var a = new LiteDatabase(shared))
            using (var peer = new LiteDatabase(new SharedEngine(Settings(first, password))))
            using (var b = new LiteDatabase(new SharedEngine(Settings(second, password))))
            {
                using (var anchor = shared.Query("rows", new Query()))
                {
                    Assert.True(anchor.Read());
                    AssertLeased(shared);
                    IEnumerable<BsonDocument> Inner()
                    {
                        yield return Row(3);
                        // The immediately enclosing ordinary call is another database;
                        // the outer pinned caller remains a dependency for its peer.
                        RefuseBeforeAdmission(peer, shared);
                        throw new ApplicationException("input failed");
                    }
                    IEnumerable<BsonDocument> Outer()
                    {
                        yield return Row(3);
                        Assert.Throws<ApplicationException>(() => b.GetCollection("rows").Insert(Inner()));
                        RefuseBeforeAdmission(peer, shared);
                        // The exceptional inner call must leave no stale dependency
                        // for its own database, and must not blanket-refuse callbacks.
                        using var independent = b.BeginTransaction();
                        independent.GetCollection("rows").Insert(Row(4));
                        independent.Commit();
                        yield return Row(4);
                    }
                    Assert.Equal(2, shared.Insert("rows", Outer(), BsonAutoId.Int32));
                }
                AssertScopesEmpty();
                PeerWrite(a);
                PeerWrite(b);
            }
            Verify(first, password, new[] { 1, 2, 3, 4, 5 });
            Verify(second, password, new[] { 1, 2, 4, 5 });
        }

        [Theory]
        [InlineData(null)] [InlineData("secret")]
        public void Initial_query_callback_refuses_peer_before_snapshot_publication(string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            Action callback = null;
            var settings = Settings(file, password);
            settings.ReadTransform = (_, value) =>
            {
                if (value.IsDocument && value["_id"] == 1) callback?.Invoke();
                return value;
            };
            using (var shared = new SharedEngine(settings))
            using (var db = new LiteDatabase(shared))
            using (var peer = new LiteDatabase(new SharedEngine(Settings(file, password))))
            {
                var calls = 0;
                callback = () =>
                {
                    calls++;
                    // The query will return an independent lease, but its initial
                    // callback still executes before the opening owner can release.
                    Assert.True(shared.MutexOwner.IsOwnedByCurrentThread);
                    RefuseBeforeAdmission(peer, shared);
                };
                using (var reader = shared.Query("rows", new Query()))
                {
                    Assert.Equal(1, calls);
                    Assert.True(reader.Read());
                    AssertLeased(shared);
                    callback = null;
                }
                AssertScopesEmpty();
                PeerWrite(peer);
            }
            Verify(file, password, new[] { 1, 2, 5 });
        }

        [Theory]
        [InlineData(null)] [InlineData("secret")]
        public void Throwing_transferred_reader_callback_restores_scope_and_releases_owner(string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            Action callback = null;
            var settings = Settings(file, password);
            settings.ReadTransform = (_, value) =>
            {
                if (value.IsDocument && value["_id"] == 2) callback?.Invoke();
                return value;
            };
            using (var shared = new SharedEngine(settings))
            using (var db = new LiteDatabase(shared))
            using (var peer = new LiteDatabase(new SharedEngine(Settings(file, password))))
            {
                using (var reader = shared.Query("rows", new Query { ForUpdate = true }))
                {
                    Assert.True(reader.Read());
                    var calls = 0;
                    callback = () =>
                    {
                        calls++;
                        RefuseBeforeAdmission(peer, shared);
                        throw new ApplicationException("read failed");
                    };
                    OnThread(() =>
                    {
                        Assert.Throws<ApplicationException>(() => reader.Read());
                        AssertScopesEmpty();
                    });
                    Assert.Equal(1, calls);
                    callback = null;
                }
                using (var tx = db.BeginTransaction())
                {
                    tx.GetCollection("rows").Insert(Row(3));
                    tx.Commit();
                }
                PeerWrite(peer);
            }
            Verify(file, password, new[] { 1, 2, 3, 5 });
        }
    }
}
