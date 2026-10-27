using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
#if !NETFRAMEWORK
using System.Threading.Tasks;
using LiteDB.Internals;
#endif

namespace LiteDB.Tests.Engine
{
    [CollectionDefinition(nameof(SharedAbandonedReaderCollection), DisableParallelization = true)]
    public sealed class SharedAbandonedReaderCollection { }

    [Collection(nameof(SharedAbandonedReaderCollection))]
    public class SharedAbandonedReader_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Collected_stream_releases_pages_and_lease_before_reopen(bool closeFacade, bool ownerExits)
        {
            using var file = new TempFile();
            Seed(file.Filename);
            using var engine = new SharedEngine(new EngineSettings { Filename = file.Filename });
            var finalized = 0;
            PageBuffer.FinalizedInUse = _ => Interlocked.Increment(ref finalized);
            try
            {
                WeakReference weak = null;
                if (ownerExits)
                {
                    Exception error = null;
                    var owner = new Thread(() =>
                    {
                        try { weak = Abandon(engine); }
                        catch (Exception ex) { error = ex; }
                    }) { IsBackground = true };
                    owner.Start();
                    owner.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    error.Should().BeNull();
                }
                else weak = Abandon(engine);
                if (closeFacade) engine.Dispose();
                Collect();
                weak.IsAlive.Should().BeFalse();
                finalized.Should().Be(0, "an abandoned streamed result must dispose its pinned pages before they finalize");
                WaitForReaders(engine);
                engine.Dispose();
                Collect();
                finalized.Should().Be(0);
                using (var writer = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared }))
                {
                    Verify(writer);
                    writer.GetCollection("other").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "peer" });
                }
                using var reopened = new LiteDatabase(file.Filename);
                Verify(reopened);
                reopened.GetCollection("other").FindById(1)["value"].AsString.Should().Be("peer");
            }
            finally { Collect(); PageBuffer.FinalizedInUse = null; }
        }

#if !NETFRAMEWORK
        [Fact]
        public async Task Collected_stream_allows_a_separate_process_to_write_and_checkpoint()
        {
            using var file = new TempFile();
            Seed(file.Filename);
            using var engine = new SharedEngine(new EngineSettings { Filename = file.Filename });
            var weak = Abandon(engine);
            Collect();
            weak.IsAlive.Should().BeFalse();
            WaitForReaders(engine);
            engine.Dispose();
            await MvccProcess.Run("insert", file.Filename, null, "1000");
            await MvccProcess.Run("checkpoint", file.Filename, null);
            using var reopened = new LiteDatabase(file.Filename);
            Verify(reopened, remoteRows: true);
            reopened.GetCollection("docs").Find(Query.GTE("_id", 1000)).Select(row => row["_id"].AsInt32)
                .OrderBy(id => id).Should().Equal(Enumerable.Range(1000, 20));
        }
#endif

        [Fact]
        public void Collecting_one_result_preserves_another_live_snapshot()
        {
            using var file = new TempFile();
            Seed(file.Filename);
            using var engine = new SharedEngine(new EngineSettings { Filename = file.Filename });
            var weak = Abandon(engine);
            using (var live = engine.Query("docs", new Query()))
            {
                Collect();
                weak.IsAlive.Should().BeFalse();
                var ids = new List<int>();
                while (live.Read())
                {
                    ids.Add(live.Current["_id"].AsInt32);
                    live.Current["payload"].AsString.Should().Be(new string('x', 1024));
                }
                ids.OrderBy(id => id).Should().Equal(Enumerable.Range(1, 160));
            }
            WaitForReaders(engine);
            engine.Dispose();
            using var reopened = new LiteDatabase(file.Filename);
            Verify(reopened);
        }

        [Fact]
        public void Collected_mutex_reader_does_not_end_its_callers_explicit_transaction()
        {
            using var file = new TempFile();
            Seed(file.Filename);
            using var engine = new SharedEngine(new EngineSettings { Filename = file.Filename });
            engine.BeginTrans().Should().BeTrue();
            engine.Insert("other", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
            var weak = Abandon(engine);
            Collect();
            weak.IsAlive.Should().BeFalse();
            var users = typeof(SharedEngine).GetField("_databaseUsers", BindingFlags.Instance | BindingFlags.NonPublic);
            var gate = typeof(SharedEngine).GetField("_useLock", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            SpinWait.SpinUntil(() => { lock (gate) return (int)users.GetValue(engine) == 1; }, TimeSpan.FromSeconds(5))
                .Should().BeTrue("the reaper must have released the result's engine user before transaction completion");
            // Rollback must still own the transaction after off-thread reader cleanup.
            engine.Rollback().Should().BeTrue();
            engine.Dispose();
            using var reopened = new LiteDatabase(file.Filename);
            Verify(reopened);
            reopened.GetCollection("other").Count().Should().Be(0);
        }

        internal static void Seed(string filename)
        {
            using var db = new LiteDatabase(filename);
            db.GetCollection("docs").Insert(Enumerable.Range(1, 160).Select(id =>
                new BsonDocument { ["_id"] = id, ["payload"] = new string('x', 1024), ["group"] = id % 2 }));
            db.GetCollection("docs").EnsureIndex("group");
        }

        private static void Verify(LiteDatabase db, bool remoteRows = false)
        {
            var rows = db.GetCollection("docs").FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
            rows.Select(row => row["_id"].AsInt32).Should().Equal(Enumerable.Range(1, 160)
                .Concat(remoteRows ? Enumerable.Range(1000, 20) : Enumerable.Empty<int>()));
            rows.Where(row => row["_id"].AsInt32 <= 160).Should()
                .OnlyContain(row => row["payload"].AsString == new string('x', 1024));
            db.GetCollection("docs").Find(Query.EQ("group", 1)).Select(row => row["_id"].AsInt32)
                .OrderBy(id => id).Should().Equal(Enumerable.Range(1, 160).Where(id => id % 2 == 1));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static WeakReference Abandon(SharedEngine engine)
        {
            var reader = engine.Query("docs", new Query());
            reader.Should().BeOfType<SharedDataReader>("the result must exceed the buffering budget");
            reader.Read().Should().BeTrue();
            return new WeakReference(reader);
        }

        internal static void Collect()
        {
            for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        }

        private static void WaitForReaders(SharedEngine engine)
        {
            var owners = (Dictionary<int, int>)typeof(SharedEngine)
                .GetField("_localReaders", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            SpinWait.SpinUntil(() => { lock (owners) return owners.Count == 0; }, TimeSpan.FromSeconds(5))
                .Should().BeTrue("deferred cleanup must release the reader registration");
        }
    }
}
