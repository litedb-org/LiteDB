#if !NETFRAMEWORK
#pragma warning disable LITEDB_EXPERIMENTAL_COORDINATOR
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Coordinated;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Platform limits the coordinator must respect on every host, exercised everywhere:
    /// Unix domain socket paths (sun_path) and 64-bit reads of a read-only mapped view
    /// on 32-bit runtimes.
    /// </summary>
    [Collection(Collection)]
    public class CoordinatorPlatform_Tests
    {
        /// <summary>In-process coordinator classes run one at a time: some tests lower process-wide protocol limits.</summary>
        internal const string Collection = "coordinator in-process";

        private const string MacTemp = "/var/folders/36/tjdph2t965j8snz9_vkdnw0r0000gn/T/";

        [Fact]
        public void Unix_pipe_names_fit_a_macOS_temp_directory()
        {
            var name = CoordinatorProtocol.PipeName("/data/app/db.db", windows: false, tempPath: MacTemp);

            name.Should().StartWith("lc-").And.HaveLength(27);
            Encoding.UTF8.GetByteCount(Path.Combine(MacTemp, "CoreFxPipe_" + name))
                .Should().BeLessOrEqualTo(CoordinatorProtocol.MaxUnixSocketPath);
        }

        [Fact]
        public void Unix_pipe_names_fall_back_to_a_rooted_tmp_path_when_the_temp_path_is_too_long()
        {
            var longTemp = "/" + new string('t', 120) + "/";
            var name = CoordinatorProtocol.PipeName("/data/app/db.db", windows: false, tempPath: longTemp);

            name.Should().StartWith("/tmp/lc-");
            Encoding.UTF8.GetByteCount(name).Should().BeLessOrEqualTo(CoordinatorProtocol.MaxUnixSocketPath);
        }

        [Fact]
        public void Pipe_names_identify_one_database()
        {
            var a = CoordinatorProtocol.PipeName("/data/a.db", windows: false, tempPath: MacTemp);
            CoordinatorProtocol.PipeName("/data/a.db", windows: false, tempPath: MacTemp).Should().Be(a);
            CoordinatorProtocol.PipeName("/data/b.db", windows: false, tempPath: MacTemp).Should().NotBe(a);
            CoordinatorProtocol.PipeName(@"C:\data\a.db", windows: true, tempPath: @"C:\Temp\")
                .Should().StartWith("litedb-coord-").And.HaveLength("litedb-coord-".Length + 40);
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(1L)]
        [InlineData(0xFFFFFFFFL)]
        [InlineData(0x1_0000_0000L)]
        [InlineData(0x7FFF_FFFF_FFFF_FFFFL)]
        [InlineData(-2L)]
        public void Split_loads_return_the_stored_64_bit_value(long value)
        {
            var memory = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt64(memory, value);
                CoordinatorStatusPage.LoadSplit(memory).Should().Be(value);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        /// <summary>
        /// On 32-bit runtimes Volatile.Read(ref long) is an interlocked compare-exchange, a
        /// write that faulted on a client's read-only status page and terminated the process
        /// (all Windows x86 CI jobs). The split path must serve every client read.
        /// </summary>
        [Fact]
        public void Client_reads_through_the_read_only_status_page_with_split_loads()
        {
            using var file = new TempFile();
            CoordinatorStatusPage.ForceSplitLoads = true;
            try
            {
                using var host = new CoordinatedEngine(file.Filename);
                using var client = new CoordinatedEngine(file.Filename);
                using var clientDb = new LiteDatabase(client, disposeOnClose: false);
                var items = clientDb.GetCollection("items");
                items.Insert(new BsonDocument { ["_id"] = 1, ["name"] = "a" });
                items.EnsureIndex("name");

                var before = client.DirectReads;
                items.FindById(1)["name"].AsString.Should().Be("a");
                client.DirectReads.Should().BeGreaterThan(before, "the read used the status page and a direct snapshot");

                using var page = CoordinatorStatusPage.TryOpen(file.Filename);
                page.TryRead(out var status).Should().BeTrue();
                status.Instance.Should().NotBe(0);
            }
            finally { CoordinatorStatusPage.ForceSplitLoads = false; }
        }
        /// <summary>
        /// Query replies over IPC are sent in bounded chunks, so a result larger than the
        /// per-message limit still arrives complete and in order.
        /// </summary>
        [Fact]
        public void Large_ipc_query_results_arrive_in_bounded_chunks()
        {
            var limit = CoordinatorProtocol.MaxRowChunkBytes;
            var messageLimit = CoordinatorProtocol.MaxMessageBytes;
            CoordinatorProtocol.MaxRowChunkBytes = 4096;
            // The whole result is about 100 KB: one frame would exceed this limit.
            CoordinatorProtocol.MaxMessageBytes = 16 * 1024;
            try
            {
                using var file = new TempFile();
                using var host = new CoordinatedEngine(file.Filename);
                using var client = new CoordinatedEngine(file.Filename);
                using var hostDb = new LiteDatabase(host, disposeOnClose: false);
                using var clientDb = new LiteDatabase(client, disposeOnClose: false);
                hostDb.GetCollection("docs").Insert(Enumerable.Range(1, 300)
                    .Select(i => new BsonDocument { ["_id"] = i, ["payload"] = new string('p', 300) }));

                var chunks = CoordinatorProtocol.Chunk(new BsonArray(hostDb.GetCollection("docs").FindAll()));
                chunks.Count.Should().BeGreaterThan(10);
                chunks.Should().OnlyContain(chunk => chunk.Sum(row => row.GetBytesCount(true)) <= 4096);

                // Inside a transaction the client reads over IPC.
                clientDb.BeginTrans().Should().BeTrue();
                var ids = clientDb.GetCollection("docs").FindAll().Select(doc => doc["_id"].AsInt32).ToList();
                clientDb.Rollback();
                ids.Should().Equal(Enumerable.Range(1, 300));
            }
            finally
            {
                CoordinatorProtocol.MaxRowChunkBytes = limit;
                CoordinatorProtocol.MaxMessageBytes = messageLimit;
            }
        }

        /// <summary>
        /// On Unix the status page lives in a private 0700 directory, because the temp directory
        /// may be the shared /tmp: a pre-created symlink or a directory others can write into
        /// must be rejected, so no forged page can serve stale snapshots.
        /// </summary>
        [Fact]
        public void Unix_status_page_directory_must_be_a_private_real_directory()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                CoordinatorStatusPage.EnsurePrivateDirectory(Path.GetTempPath(), create: true).Should().BeTrue();
                return;
            }
            var root = Path.Combine(Path.GetTempPath(), "litedb-coordpage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var fresh = Path.Combine(root, "fresh");
                CoordinatorStatusPage.EnsurePrivateDirectory(fresh, create: false).Should().BeFalse("clients never create it");
                CoordinatorStatusPage.EnsurePrivateDirectory(fresh, create: true).Should().BeTrue();
                File.GetUnixFileMode(fresh).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                var open = Path.Combine(root, "open");
                Directory.CreateDirectory(open);
                File.SetUnixFileMode(open, (UnixFileMode)0x1FF);
                ((Action)(() => CoordinatorStatusPage.EnsurePrivateDirectory(open, create: true)))
                    .Should().Throw<UnauthorizedAccessException>("others can write into a 0777 directory");

                var link = Path.Combine(root, "link");
                Directory.CreateSymbolicLink(link, fresh);
                ((Action)(() => CoordinatorStatusPage.EnsurePrivateDirectory(link, create: false)))
                    .Should().Throw<UnauthorizedAccessException>("a symlink may point anywhere");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        /// <summary>
        /// A directory another account planted in a shared temp directory stays under its
        /// control (it can change the mode after any check), so the page's private directory
        /// only lives in a base that nobody else can write to.
        /// </summary>
        [Fact]
        public void Status_page_base_must_not_be_writable_by_others()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            var root = Path.Combine(Path.GetTempPath(), "litedb-coordbase-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var shared = Path.Combine(root, "shared");
                Directory.CreateDirectory(shared);
                File.SetUnixFileMode(shared, (UnixFileMode)0x3FF); // 1777, like /tmp
                var group = Path.Combine(root, "group");
                Directory.CreateDirectory(group);
                File.SetUnixFileMode(group, (UnixFileMode)0x1F8); // 0770
                var owned = Path.Combine(root, "owned");
                Directory.CreateDirectory(owned);
                File.SetUnixFileMode(owned, (UnixFileMode)0x1C0); // 0700, like XDG_RUNTIME_DIR

                CoordinatorStatusPage.TrustedBase(new[] { null, "", "relative", shared, group, Path.Combine(root, "missing"), owned })
                    .Should().Be(owned);
                CoordinatorStatusPage.TrustedBase(new[] { shared, group }).Should().BeNull("no page beats a plantable one");
                CoordinatorStatusPage.TrustedBase(new[] { "/tmp" }).Should().BeNull("the shared /tmp is 1777");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        /// <summary>Wherever this environment puts the page, nobody else can write its base.</summary>
        [Fact]
        public void Status_page_never_lives_under_a_directory_others_can_write()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            string path;
            try { path = CoordinatorStatusPage.PathFor("/data/app.db"); }
            catch (UnauthorizedAccessException) { return; } // no trusted base: IPC grants only
            var baseDirectory = Path.GetDirectoryName(Path.GetDirectoryName(path));
            (File.GetUnixFileMode(baseDirectory) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite))
                .Should().Be((UnixFileMode)0);
        }

#if DEBUG || TESTING
        /// <summary>
        /// Stop can find the accept thread creating its next pipe instance, which may then fail
        /// with an IOException (busy instances, failed bind). That exception must end the loop,
        /// not escape the background thread and terminate the process.
        /// </summary>
        [Fact]
        public void A_pipe_failure_during_stop_ends_the_accept_loop()
        {
            using var file = new TempFile();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var armed = 0;
            CoordinatorHost.BeforeCreatePipe = (filename, token) =>
            {
                if (filename != file.Filename || Volatile.Read(ref armed) == 0) return;
                Volatile.Write(ref armed, 0);
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                if (token.IsCancellationRequested) throw new IOException("All pipe instances are busy.");
            };
            try
            {
                var host = new CoordinatedEngine(file.Filename);
                host.IsCoordinator.Should().BeTrue();
                // A first session proves the accept loop is past its initial pipe instance, so the
                // armed hook stops the instance it creates after the next session connects.
                using (var warm = new CoordinatedEngine(file.Filename))
                    warm.Insert("rows", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32).Should().Be(1);
                Volatile.Write(ref armed, 1);
                using (var client = new CoordinatedEngine(file.Filename))
                {
                    // Serving this session makes the accept loop create its next pipe instance.
                    client.Insert("rows", new[] { new BsonDocument { ["_id"] = 2 } }, BsonAutoId.Int32).Should().Be(1);
                }
                entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

                var stop = Task.Run(host.Dispose);
                Thread.Sleep(200);
                release.Set();
                stop.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("Stop joins the accept thread");
                host.HostForTests?.AcceptFailure.Should().BeNull("an IOException during stop is a stop, not a failure");
            }
            finally
            {
                CoordinatorHost.BeforeCreatePipe = null;
                release.Set();
            }
        }
#endif
    }
}
#endif
