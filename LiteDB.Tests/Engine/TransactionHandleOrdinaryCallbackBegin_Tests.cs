using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using LiteDB.Client.Shared;
using Xunit;
using static LiteDB.Tests.Engine.TransactionHandleSharedCallback_Tests;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleOrdinaryCallbackBegin_Tests
    {
        [Theory]
        [InlineData(null, false)] [InlineData("secret", false)]
        [InlineData(null, true)] [InlineData("secret", true)]
        public void Pinned_input_without_holds_refuses_handle_before_admission(string password, bool peerFacade)
        {
            using var file = new TempFile();
            Seed(file, password);
            var settings = Settings(file, password);
            settings.ReadTransform = (_, value) => value;
            using (var shared = new SharedEngine(settings))
            using (var db = new LiteDatabase(shared))
            using (var peer = new LiteDatabase(new SharedEngine(Settings(file, password))))
            {
                using (var anchor = shared.Query("rows", new Query()))
                {
                    Assert.True(anchor.Read());
                    AssertLeased(shared);
                    var calls = 0;
                    IEnumerable<BsonDocument> Input()
                    {
                        yield return Row(3);
                        calls++;
                        var pin = Assert.IsType<SharedMutexPin>(Field(shared, "_pin"));
                        Assert.True((int)Field(pin, "_operations") > 0);
                        Assert.Equal(0, Field(pin, "_holds"));
                        Assert.False(shared.MutexOwner.IsOwnedByCurrentThread);
                        Assert.False(pin.IsHeldByCurrentThread);
                        RefuseBeforeAdmission(peerFacade ? peer : db, shared);
                        yield return Row(4);
                    }
                    Assert.Equal(2, shared.Insert("rows", Input(), BsonAutoId.Int32));
                    Assert.Equal(1, calls);
                    // A returned pin operation can now retire independently. Its
                    // earlier callback scope must not reject an idle begin.
                    using var idle = db.BeginTransaction();
                    idle.GetCollection("rows").Insert(Row(6));
                    idle.Rollback();
                }
                AssertScopesEmpty();
                PeerWrite(db);
            }
            Verify(file, password, new[] { 1, 2, 3, 4, 5 });
        }

        [Theory]
        [InlineData(null, false, false)] [InlineData("secret", false, false)]
        [InlineData(null, true, false)] [InlineData("secret", true, false)]
        [InlineData(null, false, true)] [InlineData("secret", false, true)]
        [InlineData(null, true, true)] [InlineData("secret", true, true)]
        public void Transferred_mutex_reader_refuses_handle_before_admission(string password, bool lockingQuery, bool peerFacade)
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
            if (!lockingQuery) settings.SharedReaderFiles = (_, __) => throw new UnauthorizedAccessException("registry denied");
            using (var shared = new SharedEngine(settings))
            using (var db = new LiteDatabase(shared))
            using (var peer = new LiteDatabase(new SharedEngine(Settings(file, password))))
            {
                using (var reader = shared.Query("rows", new Query { ForUpdate = lockingQuery }))
                {
                    Assert.True(reader.Read());
                    Assert.Equal(1, reader.Current["_id"].AsInt32);
                    var calls = 0;
                    callback = () =>
                    {
                        calls++;
                        Assert.False(shared.MutexOwner.IsOwnedByCurrentThread);
                        Assert.Null(Field(shared, "_pin"));
                        RefuseBeforeAdmission(peerFacade ? peer : db, shared);
                    };
                    OnThread(() => Assert.True(reader.Read()));
                    Assert.Equal(2, reader.Current["_id"].AsInt32);
                    Assert.Equal(1, calls);
                    callback = null;
                }
                AssertScopesEmpty();
                PeerWrite(db);
            }
            Verify(file, password, new[] { 1, 2, 5 });
        }

        [Theory]
        [InlineData(null, false)] [InlineData("secret", false)]
        [InlineData(null, true)] [InlineData("secret", true)]
        public void Independent_leased_reader_callback_can_begin_handle(string password, bool transferred)
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
            {
                using (var reader = shared.Query("rows", new Query()))
                {
                    Assert.True(reader.Read());
                    AssertLeased(shared);
                    var calls = 0;
                    callback = () =>
                    {
                        calls++;
                        using var tx = db.BeginTransaction();
                        tx.GetCollection("rows").Insert(Row(3));
                        tx.Commit();
                    };
                    if (transferred) OnThread(() => Assert.True(reader.Read()));
                    else Assert.True(reader.Read());
                    Assert.Equal(2, reader.Current["_id"].AsInt32);
                    Assert.Equal(1, calls);
                    callback = null;
                }
                PeerWrite(db);
            }
            Verify(file, password, new[] { 1, 2, 3, 5 });
        }

        internal static void RefuseBeforeAdmission(LiteDatabase db, SharedEngine shared)
        {
            // The refusal precedes local and native admission; a regression waits here.
            var refusal = Record.Exception(() =>
            {
                using var nested = db.BeginTransaction();
                nested.GetCollection("ordinary").Insert(Row(99));
                nested.Commit();
            });
            Assert.IsType<InvalidOperationException>(refusal);
            Assert.Equal(0, db.TransactionHandles.ActiveCount);
            var acquired = false;
            OnThread(() =>
            {
                acquired = shared.MutexOwner.Mutex.WaitOne(0);
                if (acquired) shared.MutexOwner.Mutex.ReleaseMutex();
            });
            Assert.False(acquired);
        }

        internal static void AssertLeased(SharedEngine shared)
        {
            Assert.Null(Field(shared, "_pin"));
            Assert.False(shared.MutexOwner.IsOwnedByCurrentThread);
            Assert.True(((Dictionary<int, int>)Field(shared, "_localReaders")).ContainsKey(Environment.CurrentManagedThreadId));
        }

        internal static void AssertScopesEmpty()
        {
            var frames = typeof(LiteDB.Client.Shared.SharedCallFrames).GetField("_frames", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            if (frames != null) Assert.Equal(0, ((System.Collections.ICollection)frames).Count);
        }

        internal static object Field(object owner, string name) => owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
    }
}
