using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.InteropServices;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2163_Tests
    {
        [Fact]
        public void Direct_owner_rejects_other_engines_without_losing_committed_rows()
        {
            using var file = new TempFile();
            using (var owner = new LiteDatabase(file.Filename))
            {
                owner.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                Action contender = () => { using var other = new LiteDatabase(file.Filename); other.GetCollection("rows").Count(); };
                contender.Should().Throw<LiteException>().WithMessage("*ownership*");
                using var shared = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared });
                contender = () => shared.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                contender.Should().Throw<LiteException>().WithMessage("*ownership*");
                owner.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 });
            }
            using var reopened = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared });
            reopened.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 4 });
            reopened.GetCollection("rows").Count().Should().Be(3);
        }

        [Fact]
        public void Shared_transaction_owns_the_file_until_completion()
        {
            using var file = new TempFile();
            using var shared = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared });
            shared.BeginTrans().Should().BeTrue();
            shared.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            Action contender = () => { using var other = new LiteDatabase(file.Filename); other.GetCollection("rows").Count(); };
            contender.Should().Throw<LiteException>().WithMessage("*ownership*");
            shared.Commit().Should().BeTrue();
            using var direct = new LiteDatabase(file.Filename);
            direct.GetCollection("rows").Count().Should().Be(1);
        }

        [Fact]
        public async Task Engine_ownership_can_be_released_on_another_thread()
        {
            using var file = new TempFile();
            var engine = new LiteEngine(file.Filename);
            await Task.Run(() => engine.Dispose());
            using var next = new LiteEngine(file.Filename);
        }

        [Fact]
        public void Failed_open_releases_ownership_and_relative_paths_share_it()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, new byte[] { 1, 2, 3 });
            Action open = () => { using var engine = new LiteEngine(file.Filename); };
            open.Should().Throw<LiteException>();
            File.Delete(file.Filename);
            using var owner = new LiteEngine(file.Filename);
            var alias = Path.Combine(Path.GetDirectoryName(file.Filename), ".", Path.GetFileName(file.Filename));
            open = () => { using var engine = new LiteEngine(alias); };
            open.Should().Throw<LiteException>().WithMessage("*ownership*");
        }

        [Fact]
        public void Rebuild_close_retains_ownership_until_engine_reopens()
        {
            using var file = new TempFile();
            using var owner = new LiteEngine(file.Filename);
            owner.Close(releaseOwnership: false).Should().BeEmpty();
            Action open = () => { using var other = new LiteEngine(file.Filename); };
            open.Should().Throw<LiteException>().WithMessage("*ownership*");
            owner.Open().Should().BeTrue();
            owner.Rebuild().Should().BeGreaterThanOrEqualTo(0);
            open.Should().Throw<LiteException>().WithMessage("*ownership*");
        }

        [Fact]
        public void Abandoned_engine_closes_streams_before_releasing_ownership()
        {
            using var file = new TempFile();
            var reference = AbandonEngine(file.Filename);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            reference.IsAlive.Should().BeFalse();
            using var reopened = new LiteDatabase(file.Filename);
            Assert.NotNull(reopened.GetCollection("rows").FindById(1));
        }

        [Fact]
        public void Multiple_read_only_engines_share_ownership_and_exclude_writers()
        {
            using var file = new TempFile();
            using (var initial = new LiteDatabase(file.Filename))
                initial.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            using var first = new LiteEngine(new EngineSettings { Filename = file.Filename, ReadOnly = true });
            using (var second = new LiteEngine(new EngineSettings { Filename = file.Filename, ReadOnly = true }))
            {
                Action write = () => { using var writer = new LiteEngine(file.Filename); };
                write.Should().Throw<LiteException>().WithMessage("*ownership*");
            }
            // Closing an unrelated descriptor must not drop an OFD ownership lock.
            using (var stream = File.OpenRead(file.Filename)) stream.ReadByte();
            Action contender = () => { using var writer = new LiteEngine(file.Filename); };
            contender.Should().Throw<LiteException>().WithMessage("*ownership*");
        }

#if !NETFRAMEWORK
        [Fact]
        public void File_symlink_and_hard_link_aliases_cannot_bypass_ownership()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
            using var file = new TempFile();
            using var owner = new LiteEngine(file.Filename);
            var symbolic = file.Filename + ".symlink";
            var hard = file.Filename + ".hardlink";
            try
            {
                File.CreateSymbolicLink(symbolic, file.Filename);
                Link(file.Filename, hard).Should().Be(0);
                foreach (var alias in new[] { symbolic, hard })
                {
                    Action open = () => { using var other = new LiteEngine(alias); };
                    open.Should().Throw<LiteException>().WithMessage("*ownership*");
                }
            }
            finally
            {
                File.Delete(symbolic);
                File.Delete(hard);
            }
        }

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        private static extern int Link(string source, string target);
#endif

        [Fact]
        public async Task Dispose_waits_for_rebuild_and_closes_the_replacement()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(file.Filename);
            using var closed = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            engine.SimulateRebuildClosed = () =>
            {
                closed.Set();
                if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            };
            var rebuild = Task.Run(() => engine.Rebuild());
            closed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            using var disposing = new ManualResetEventSlim();
            var dispose = Task.Run(() => { disposing.Set(); engine.Dispose(); });
            try
            {
                disposing.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                (await Task.WhenAny(dispose, Task.Delay(100))).Should().NotBe(dispose);
                Action open = () => { using var other = new LiteEngine(file.Filename); };
                open.Should().Throw<LiteException>().WithMessage("*ownership*");
            }
            finally { proceed.Set(); }
            await Task.WhenAll(rebuild, dispose);
            using var replacement = new LiteEngine(file.Filename);
        }

        [Fact]
        public async Task Read_only_engines_keep_spilling_sorts_independent()
        {
            using var file = new TempFile();
            const int count = 3000;
            using (var setup = new LiteDatabase(file.Filename))
                setup.GetCollection("rows").Insert(Enumerable.Range(1, count).Select(id => new BsonDocument
                {
                    ["_id"] = id,
                    ["key"] = id.ToString("D6") + new string('x', 500)
                }));
            var settings = new ConnectionString { Filename = file.Filename, ReadOnly = true };
            using var first = new LiteDatabase(settings);
            using var second = new LiteDatabase(settings);
            Func<LiteDatabase, int[]> sort = db => db.GetCollection("rows").Query()
                .OrderBy("key", Query.Descending).ToArray().Select(row => row["_id"].AsInt32).ToArray();
            var expected = Enumerable.Range(1, count).Reverse().ToArray();
            var results = await Task.WhenAll(Task.Run(() => sort(first)), Task.Run(() => sort(second)));
            foreach (var result in results) result.Should().Equal(expected);
            first.Dispose();
            sort(second).Should().Equal(expected);
        }

        [Fact]
        public void Errors_from_an_old_generation_do_not_close_the_rebuilt_engine()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(file.Filename);
            var stateField = typeof(LiteEngine).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var oldState = (EngineState)stateField.GetValue(engine);
            engine.Rebuild();
            oldState.Stop(new IOException("Delayed failure from an old transaction"));
            engine.Insert("rows", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
            Action open = () => { using var other = new LiteEngine(file.Filename); };
            open.Should().Throw<LiteException>().WithMessage("*ownership*");
        }

        [Fact]
        public async Task Opener_cannot_lock_an_old_inode_then_use_the_rebuilt_file()
        {
            using var file = new TempFile();
            using var owner = new LiteEngine(file.Filename);
            using var opened = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            FileOwnership.SimulateBeforeLock = path =>
            {
                if (path != file.Filename) return;
                opened.Set();
                if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            };
            Task<Exception> contender = null;
            try
            {
                contender = Task.Run(() => Record.Exception(() => { using var other = new LiteEngine(file.Filename); }));
                opened.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                owner.Rebuild();
            }
            finally
            {
                FileOwnership.SimulateBeforeLock = null;
                proceed.Set();
            }
            var error = await contender;
            error.Should().BeOfType<LiteException>().Which.Message.Should().Contain("ownership");
            owner.Insert("rows", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
        }

        [Fact]
        public void Read_only_recovery_releases_exclusive_ownership_before_reading()
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
                setup.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var bytes = File.ReadAllBytes(file.Filename);
            bytes[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
            File.WriteAllBytes(file.Filename, bytes);
            using var recovered = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename, ReadOnly = true, AutoRebuild = true
            });
            recovered.GetCollection("rows").Count().Should().Be(1);
            using var parallel = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true });
            parallel.GetCollection("rows").Count().Should().Be(1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference AbandonEngine(string filename)
        {
            var engine = new LiteEngine(filename);
            engine.Insert("rows", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
            return new WeakReference(engine);
        }
    }
}
