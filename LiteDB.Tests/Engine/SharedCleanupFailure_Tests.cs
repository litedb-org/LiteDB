#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Reflection;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedCleanupFailure_Tests
    {
        [MappedFact]
        public void Timer_cleanup_errors_do_not_escape_or_leave_a_reusable_snapshot()
        {
            using var file = new MappedTestFile();
            using (var engine = new SharedEngine(new EngineSettings { Filename = file })
                { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
            using (var database = new LiteDatabase(engine))
            {
                database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 19 });
                for (var i = 0; i < 3; i++) database.GetCollection("rows").FindById(1);
                var snapshot = Field<object>(engine, "_cachedSnapshot");
                snapshot.Should().NotBeNull();
                var failing = new FailingLease();
                snapshot.GetType().GetProperty("Lease", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(snapshot, failing);
                engine.CoordinatedIdleLimit = TimeSpan.Zero;
                // Invoke the actual timer entry point deterministically. Before the
                // guard this exception would escape a ThreadPool timer callback.
                Action expire = () => typeof(SharedEngine).GetMethod("ExpireSnapshot",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine, null);
                expire.Should().NotThrow();
                failing.Disposed.Should().BeTrue();
                engine.HasCachedSnapshot.Should().BeFalse();
                Field<object>(engine, "_snapshotIdle").Should().BeNull();
                database.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(19);
            }
            using var cold = new LiteDatabase(file);
            cold.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(19);
        }

        [MappedFact]
        public void Page_finalization_contains_managed_disposal_errors_and_releases_participation()
        {
            using var file = new MappedTestFile();
            var failing = AbandonPage(file, out var page);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            page.IsAlive.Should().BeFalse();
            failing.Closed.Should().BeTrue();
            using var exclusive = new FileStream(SharedCoordinationFallback.LivePath(file), FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
            GC.KeepAlive(failing);
        }

        // A separate frame prevents JIT/AOT lifetime extension from retaining the page.
        // Keep the injected stream alive so only the page's real finalizer can close it.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static FailingStream AbandonPage(string file, out WeakReference reference)
        {
            var page = SharedCoordinationPage.Open(file);
            var original = Field<FileStream>(page, "_participation");
            var failing = new FailingStream(SharedCoordinationFallback.LivePath(file));
            typeof(SharedCoordinationPage).GetField("_participation", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(page, failing);
            original.Dispose();
            reference = new WeakReference(page);
            return failing;
        }

        [MappedTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Failed_open_publication_closes_the_engine_and_preserves_the_original_error(bool recovering)
        {
            using var file = new MappedTestFile();
            using (var engine = new SharedEngine(new EngineSettings { Filename = file }))
            using (var database = new LiteDatabase(engine))
            {
                database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 19 });
                database.GetCollection("rows").FindById(1);
                var page = Field<SharedCoordinationPage>(engine, "_coordination");
                page.Should().NotBeNull();
                var view = Field<System.IO.MemoryMappedFiles.MemoryMappedViewAccessor>(page, "_view");
                var originalVersion = view.ReadInt64(8);
                if (recovering) view.Write(SharedCoordinationProtocol.StructuralOffset, 1L);
                engine.CoordinationStage = stage =>
                {
                    if (stage != "opened") return;
                    view.Write(8, 999L); // Opened and StructuralEnd both reject this header.
                    if (recovering) throw new IOException("original open failure");
                };
                Action update = () => database.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["value"] = 20 });
                if (recovering) update.Should().Throw<IOException>().WithMessage("original open failure");
                else update.Should().Throw<IOException>().WithMessage("*header changed*");
                Field<LiteEngine>(engine, "_engine").Should().BeNull();
                Field<int>(engine, "_databaseUsers").Should().Be(0);
                engine.CoordinationStage = null;
                view.Write(8, originalVersion);
                engine.Dispose();
            }
            using var cold = new LiteDatabase(file);
            cold.GetCollection("rows").Count().Should().Be(1);
            cold.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(19);
            cold.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = 23 });
        }

        private static T Field<T>(object owner, string name) =>
            (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);

        private sealed class FailingLease : IDisposable
        {
            internal bool Disposed;
            public void Dispose() { Disposed = true; throw new ObjectDisposedException("injected lease close"); }
        }

        private sealed class FailingStream : FileStream
        {
            internal bool Closed;
            internal FailingStream(string path) : base(path, FileMode.Open, FileAccess.Read, FileShare.Read) { }
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                Closed = true;
                if (disposing) throw new IOException("injected participation close");
            }
        }
    }
}
#endif
